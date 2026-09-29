using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record NaturalStageBlock(string Id, string Title, IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> NodeIds, IReadOnlyList<ControlScope> ControlPath);
internal sealed record NaturalStagedPlan(string Title, IReadOnlyList<NaturalDesignNode> Nodes,
    IReadOnlyList<NaturalDesignEdge> Edges, IReadOnlyList<string> Notes, IReadOnlyList<NaturalStageBlock> Blocks)
{
    internal NaturalDesign Skeleton => new(Title, Nodes, Edges, Notes);
}
internal sealed record NaturalBlockDivision(IReadOnlyList<NaturalStageBlock> Blocks);

internal static class NaturalStagedDesign
{
    internal static NaturalStagedPlan Complete(NaturalStagedPlan plan, string type)
    {
        var normalized = NaturalDesignValidation.CompleteInapplicableFields(plan.Skeleton, type);
        return plan with { Nodes = normalized.Nodes, Edges = normalized.Edges };
    }
    internal const int MaximumBlocks = 32;
    internal static JsonElement Schema(string type, bool division = false)
    {
        var schema = division ? new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(), ["required"] = new JsonArray() }
            : JsonNode.Parse(NaturalDesignValidation.DesignSchemaFor(type).GetRawText())!;
        var controls = JsonNode.Parse(NaturalDesignValidation.DesignSchemaFor("sequence").GetRawText())!
            ["properties"]!["edges"]!["items"]!["properties"]!["controlPath"]!.DeepClone();
        schema["properties"]!["blocks"] = new JsonObject {
            ["type"] = "array", ["minItems"] = division ? 2 : 1, ["maxItems"] = MaximumBlocks,
            ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("id", "title", "requirementIds", "nodeIds", "controlPath"),
                ["properties"] = new JsonObject {
                    ["id"] = new JsonObject { ["type"] = "string", ["maxLength"] = 60 },
                    ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
                    ["requirementIds"] = JsonNode.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":150,\"items\":{\"type\":\"string\"}}"),
                    ["nodeIds"] = JsonNode.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"string\"}}"),
                    ["controlPath"] = controls } } };
        schema["required"]!.AsArray().Add("blocks");
        return JsonSerializer.SerializeToElement(schema);
    }

    internal static string? Check(NaturalStagedPlan plan, string type, NaturalRequirements requirements)
    {
        if (plan is null) return "NaturalDesignFieldsInvalid";
        plan = Complete(plan, type);
        if (NaturalDesignValidation.BasicIssues(plan.Skeleton).Count > 0 || plan.Nodes.Count == 0)
            return "NaturalDesignFieldsInvalid";
        if (plan.Nodes.Any(n => type == "sequence" ? n.Kind != "participant" : n.Kind is not ("class" or "interface")))
            return "NaturalNodeInvalid";
        if (type == "sequence" && plan.Edges.Count > 0 || type == "class" && plan.Nodes.Any(n => n.Members.Count > 0))
            return "NaturalIntegrationScopeInvalid";
        var known = requirements.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (plan.Nodes.SelectMany(n => n.RequirementIds).Concat(plan.Edges.SelectMany(e => e.RequirementIds)).Any(id => !known.Contains(id)))
            return "NaturalNodeRequirementIdsUnknown";
        var blockError = CheckBlocks(plan.Blocks, plan.Nodes.Select(n => n.Id).ToHashSet(), known, type);
        if (blockError is not null) return blockError;
        var covered = plan.Blocks.SelectMany(b => b.RequirementIds).ToHashSet();
        if (requirements.Requirements.Any(r => !covered.Contains(r.Id))) return "NaturalRequirementCoverageMissing";
        if (type == "class" && plan.Nodes.Any(n => !plan.Blocks.Any(b => b.NodeIds.Contains(n.Id))))
            return "NaturalRequirementCoverageMissing";
        return null;
    }

    internal static string? CheckBlocks(IReadOnlyList<NaturalStageBlock>? blocks, IReadOnlySet<string> nodes,
        IReadOnlySet<string> requirements, string type)
    {
        if (blocks is not { Count: > 0 and <= MaximumBlocks } || blocks.Any(b => b is null || string.IsNullOrWhiteSpace(b.Id) ||
            string.IsNullOrWhiteSpace(b.Title) || b.RequirementIds is not { Count: > 0 } || b.NodeIds is not { Count: > 0 } ||
            b.RequirementIds.Distinct().Count() != b.RequirementIds.Count || b.NodeIds.Distinct().Count() != b.NodeIds.Count ||
            b.ControlPath is null || b.ControlPath.Count > 32 || b.ControlPath.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) ||
                string.IsNullOrWhiteSpace(c.Label) || c.Kind is not ("alt" or "opt" or "loop"))))
            return "NaturalDesignFieldsInvalid";
        if (blocks.Select(b => b.Id).Distinct().Count() != blocks.Count) return "DuplicateId";
        if (blocks.Any(b => b.NodeIds.Any(id => !nodes.Contains(id)) || b.RequirementIds.Any(id => !requirements.Contains(id))))
            return "NaturalMeaningReferencesInvalid";
        if (type == "class" && blocks.Any(b => b.NodeIds.Count != 1 || b.ControlPath.Count > 0)) return "NaturalIntegrationScopeInvalid";
        return null;
    }

    internal static NaturalRequirements ForBlock(NaturalRequirements source, NaturalStageBlock block)
    {
        var selected = source.Requirements.Where(r => block.RequirementIds.Contains(r.Id)).ToArray();
        var ranges = selected.SelectMany(NaturalRequirementEvidence.RangeIds).ToHashSet();
        return source with { Title = block.Title, Requirements = selected,
            SourceRanges = source.SourceRanges?.Where(r => ranges.Contains(r.Id)).ToArray(),
            Scenarios = [new(block.Id, block.Title, selected.Select(r => r.Id).ToArray(), ranges.ToArray())] };
    }

    internal static string? CheckBlock(NaturalDesign value, NaturalStagedPlan plan, NaturalStageBlock block, string type)
    {
        if (value?.Nodes is null || value.Edges is null || value.Notes is null) return "NaturalDesignFieldsInvalid";
        var known = block.RequirementIds.ToHashSet();
        if (value.Nodes.Any(n => n is null || n.RequirementIds is null || n.Members is null || n.Details is null) ||
            value.Edges.Any(e => e is null || e.RequirementIds is null || e.ControlPath is null)) return "NaturalDesignFieldsInvalid";
        if (value.Nodes.SelectMany(n => n.RequirementIds).Concat(value.Edges.SelectMany(e => e.RequirementIds)).Any(id => !known.Contains(id)))
            return "NaturalNodeRequirementIdsUnknown";
        if (type == "sequence")
        {
            if (value.Nodes.Count != 0 || value.Edges.Any(e => !block.NodeIds.Contains(e.SourceId) || !block.NodeIds.Contains(e.TargetId)))
                return "NaturalIntegrationScopeInvalid";
            if (value.Edges.Select(e => e.Id).Distinct().Count() != value.Edges.Count) return "DuplicateId";
            return null;
        }
        if (value.Nodes.Count != 1 || value.Nodes[0].Id != block.NodeIds[0] || value.Edges.Count != 0)
            return "NaturalIntegrationScopeInvalid";
        var node = value.Nodes[0];
        var original = plan.Nodes.Single(n => n.Id == node.Id);
        return node.Label != original.Label || node.Kind != original.Kind || node.Assumption != original.Assumption
            ? "NaturalIntegrationScopeInvalid" : null;
    }

    internal static NaturalDesign Assemble(NaturalStagedPlan plan, IReadOnlyList<(NaturalStageBlock Block, NaturalDesign Design)> parts, string type)
    {
        var nodes = plan.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = plan.Edges.ToList();
        foreach (var (block, part) in parts)
        {
            foreach (var node in part.Nodes)
            {
                var before = nodes[node.Id];
                nodes[node.Id] = before with {
                    Members = before.Members.Concat(node.Members).DistinctBy(m => JsonSerializer.Serialize(m)).ToArray(),
                    Details = before.Details.Concat(node.Details).Distinct().ToArray(),
                    RequirementIds = before.RequirementIds.Concat(node.RequirementIds).Distinct().ToArray() };
            }
            foreach (var (edge, index) in part.Edges.Select((edge, index) => (edge, index)))
                edges.Add(edge with { Id = "edge-" + SemanticExecution.Hash(block.Id + ":" + edge.Id + ":" + index)[..20],
                    ControlPath = block.ControlPath.Concat(edge.ControlPath.Select(c => c with {
                        Id = "control-" + SemanticExecution.Hash(block.Id + ":" + c.Id)[..20] })).ToArray() });
        }
        return new(plan.Title, nodes.Values.ToArray(), edges, plan.Notes.Concat(parts.SelectMany(p => p.Design.Notes)).Distinct().ToArray());
    }
}
