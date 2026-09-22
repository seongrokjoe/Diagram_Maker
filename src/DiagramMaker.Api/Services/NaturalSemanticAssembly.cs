using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class NaturalSemanticAssembly
{
    internal const string Version = "natural-meaning-v1";

    public static JsonElement Schema(string type)
    {
        var original = JsonNode.Parse(NaturalDesignValidation.DesignSchema.GetRawText())!;
        var concepts = original["properties"]!["nodes"]!.DeepClone();
        var connections = original["properties"]!["edges"]!.DeepClone();
        void Edit(JsonNode array, params (string Old, string? New)[] changes)
        {
            var item = array["items"]!;
            var properties = item["properties"]!.AsObject();
            foreach (var (oldName, newName) in changes)
            {
                var value = properties[oldName]!.DeepClone();
                properties.Remove(oldName);
                if (newName is not null) properties[newName] = value;
            }
            item["required"] = new JsonArray(properties.Select(p => JsonValue.Create(p.Key)).ToArray());
            array["maxItems"] = 80;
        }
        Edit(concepts, ("id", "key"), ("requirementIds", null));
        concepts["items"]!["properties"]!["members"]!["items"]!["properties"]!["assumption"] =
            new JsonObject { ["type"] = "boolean" };
        Edit(connections, ("id", null), ("sourceId", "source"), ("targetId", "target"), ("requirementIds", null));
        return JsonSerializer.SerializeToElement(new JsonObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["concepts"] = concepts, ["connections"] = connections },
            ["required"] = new JsonArray("concepts", "connections") });
    }

    public static IReadOnlyList<NaturalIssue> Validate(NaturalSemanticUnit value, string type, NaturalRequirements requirements,
        string requirementId)
    {
        if (value.Concepts is not { Count: > 0 and <= 80 } || value.Connections is null || value.Connections.Count > 80)
            return [new(requirementId, "concepts", "NaturalMeaningMissing", "Return the actual concepts and relationships implementing the target requirement.")];
        if (value.Concepts.Any(c => c is null || string.IsNullOrWhiteSpace(c.Key)) ||
            value.Concepts.Select(c => c.Key).Distinct().Count() != value.Concepts.Count)
            return [new(requirementId, "concepts", "NaturalMeaningKeysInvalid", "Use unique, nonempty local concept keys.")];
        var keys = value.Concepts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (value.Connections.Any(c => c is null || !keys.Contains(c.Source) || !keys.Contains(c.Target)))
            return [new(requirementId, "connections", "NaturalMeaningReferencesInvalid", "Every source/target must reference a concept key in this unit.")];
        var subset = requirements with { Requirements = requirements.Requirements.Where(r => r.Id == requirementId).ToArray() };
        var fragment = Assemble(new(type, subset, [new(requirementId, value, false)]));
        return NaturalDesignValidation.DesignIssues(fragment, type, subset);
    }

    public static NaturalDesign Assemble(NaturalSemanticPlan plan)
    {
        var nodes = new Dictionary<string, NaturalDesignNode>(StringComparer.Ordinal);
        var edges = new List<NaturalDesignEdge>();
        foreach (var unit in plan.Units)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var concept in unit.Meaning.Concepts)
            {
                // Only explicitly canonical entities can be shared across units.
                // Equal action labels never establish identity or execution order.
                var canonical = concept.Kind is "class" or "interface" or "participant" &&
                    plan.Requirements.Entities.Contains(concept.Label, StringComparer.Ordinal);
                var id = "n" + SemanticExecution.Hash(canonical ? $"{plan.Type}:entity:{concept.Kind}:{concept.Label}" :
                    $"{plan.Type}:{unit.RequirementId}:{concept.Key}")[..20];
                map[concept.Key] = id;
                var candidate = new NaturalDesignNode(id, concept.Label, concept.Kind, concept.Shape ?? "",
                    concept.Members ?? [], concept.Details ?? [], [unit.RequirementId], concept.Assumption);
                if (nodes.TryGetValue(id, out var previous))
                    candidate = previous with {
                        Members = previous.Members.Concat(candidate.Members).DistinctBy(m => JsonSerializer.Serialize(m)).ToArray(),
                        Details = previous.Details.Concat(candidate.Details).Distinct().ToArray(),
                        RequirementIds = previous.RequirementIds.Append(unit.RequirementId).Distinct().ToArray(),
                        Assumption = previous.Assumption || candidate.Assumption };
                nodes[id] = candidate;
            }
            foreach (var (connection, index) in unit.Meaning.Connections.Select((c, i) => (c, i)))
                edges.Add(new("e" + SemanticExecution.Hash($"{plan.Type}:{unit.RequirementId}:{index}")[..20],
                    map[connection.Source], map[connection.Target], connection.Type, connection.Label,
                    connection.Event ?? "", connection.Guard ?? "", connection.Action ?? "",
                    (connection.ControlPath ?? []).Select(c => c with { Id = "c" + SemanticExecution.Hash(unit.RequirementId + ":" + c.Id)[..20] }).ToArray(),
                    [unit.RequirementId], connection.Assumption));
        }
        return new(plan.Requirements.Title, nodes.Values.ToArray(), edges, []);
    }

    public static DiagramIr Compile(NaturalSemanticPlan plan, NaturalDesign? integrated = null)
    {
        var design = integrated ?? Assemble(plan);
        var ir = NaturalDesignValidation.Normalize(design, plan.Type);
        // Unrelated requirement units do not imply a call order. Keep each unit's
        // reviewed execution sequence in an explicitly separate scenario.
        if (plan.Type == "sequence" && plan.Units.Count > 1)
        {
            var localIds = Assemble(plan).Edges.Select(e => e.Id).ToHashSet();
            var scenarios = plan.Units.Select(unit => new SequenceBlock(
                "s" + SemanticExecution.Hash(unit.RequirementId)[..20], "scenario",
                plan.Requirements.Requirements.Single(r => r.Id == unit.RequirementId).Text,
                SequenceStructure.FromEdges(ir.Edges.Where(e => localIds.Contains(e.Id) && design.Edges.Single(d => d.Id == e.Id)
                    .RequirementIds.Contains(unit.RequirementId)).ToArray()))).ToList();
            var crossEdges = ir.Edges.Where(e => !localIds.Contains(e.Id)).ToArray();
            if (crossEdges.Length > 0) scenarios.Add(new("integration", "scenario", "요구사항 간 상호작용", SequenceStructure.FromEdges(crossEdges)));
            ir = ir with { SequenceBlocks = scenarios };
        }
        return ir with { Provenance = ir.Provenance.Append(Version).ToArray() };
    }
}
