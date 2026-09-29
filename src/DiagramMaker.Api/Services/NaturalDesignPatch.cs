using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record NaturalDesignPatch(string BaseHash, IReadOnlyList<NaturalDesignNode> Nodes,
    IReadOnlyList<NaturalDesignEdge> Edges, IReadOnlyList<string> RemovedNodeIds,
    IReadOnlyList<string> RemovedEdgeIds, IReadOnlyList<string> EdgeOrder);

internal static class NaturalDesignPatching
{
    internal static string Hash(NaturalDesign design) => SemanticExecution.Hash(JsonSerializer.Serialize(design, PromptJson.Options));

    internal static JsonElement Schema(string type, NaturalDesign design)
    {
        var source = JsonNode.Parse(NaturalDesignValidation.DesignSchemaFor(type).GetRawText())!;
        var properties = source["properties"]!.AsObject();
        properties.Remove("title"); properties.Remove("notes");
        properties["baseHash"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = new[] { Hash(design) } });
        foreach (var field in new[] { "removedNodeIds", "removedEdgeIds", "edgeOrder" })
            properties[field] = JsonSerializer.SerializeToNode(new { type = "array", maxItems = 500, items = new { type = "string", maxLength = 80 } });
        source["required"] = new JsonArray(properties.Select(p => JsonValue.Create(p.Key)).ToArray());
        return JsonSerializer.SerializeToElement(source);
    }

    internal static NaturalDesign Apply(NaturalDesign before, NaturalDesignPatch patch, string type, IReadOnlyList<NaturalIssue> issues)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
        { if (!valid) throw new ArgumentException("NaturalPatchScopeInvalid"); }
        Require(patch is not null && patch.BaseHash == Hash(before) && patch.Nodes is not null && patch.Edges is not null &&
            patch.RemovedNodeIds is not null && patch.RemovedEdgeIds is not null && patch.EdgeOrder is not null);
        var normalized = NaturalDesignValidation.CompleteInapplicableFields(new(before.Title, patch!.Nodes, patch.Edges, []), type);
        var requirements = issues.Where(i => i.TargetKind == "requirement" ||
            !before.Nodes.Any(n => n.Id == i.ItemId) && !before.Edges.Any(e => e.Id == i.ItemId) && i.ItemId != "design")
            .Select(i => i.ItemId).ToHashSet();
        bool Whole() => issues.Any(i => i.ItemId == "design" && i.Field is "structure" or "id");
        bool Affected(string id, IReadOnlyList<string> refs) => Whole() || refs.Any(requirements.Contains) ||
            issues.Any(i => i.ItemId == id || i.RelatedElementIds?.Contains(id) == true);
        void Fields<T>(string id, T oldValue, T newValue, IReadOnlyList<string> refs)
        {
            var oldJson = JsonSerializer.SerializeToElement(oldValue, PromptJson.Options);
            var newJson = JsonSerializer.SerializeToElement(newValue, PromptJson.Options);
            foreach (var property in newJson.EnumerateObject())
            {
                if (property.Value.GetRawText() == oldJson.GetProperty(property.Name).GetRawText()) continue;
                Require(property.Name != "id" && Affected(id, refs));
                Require(Whole() || refs.Any(requirements.Contains) || issues.Any(i =>
                    (i.ItemId == id || i.RelatedElementIds?.Contains(id) == true) &&
                    (i.Field == property.Name || i.Field == "structure" || i.RelatedElementIds?.Contains(id) == true)));
            }
        }
        Require(normalized.Nodes.All(n => n is not null && n.RequirementIds is not null) &&
            normalized.Edges.All(e => e is not null && e.RequirementIds is not null));
        Require(normalized.Nodes.Select(n => n.Id).Distinct().Count() == normalized.Nodes.Count &&
            normalized.Edges.Select(e => e.Id).Distinct().Count() == normalized.Edges.Count);
        var nodes = before.Nodes.ToList(); var edges = before.Edges.ToList();
        foreach (var node in normalized.Nodes)
        {
            var index = nodes.FindIndex(n => n.Id == node.Id);
            if (index < 0) { Require(Whole() || node.RequirementIds.Any(requirements.Contains)); nodes.Add(node); }
            else { Fields(node.Id, nodes[index], node, nodes[index].RequirementIds); nodes[index] = node; }
        }
        foreach (var edge in normalized.Edges)
        {
            var index = edges.FindIndex(e => e.Id == edge.Id);
            if (index < 0) { Require(Whole() || edge.RequirementIds.Any(requirements.Contains)); edges.Add(edge); }
            else { Fields(edge.Id, edges[index], edge, edges[index].RequirementIds); edges[index] = edge; }
        }
        foreach (var id in patch.RemovedNodeIds)
        {
            var node = nodes.FirstOrDefault(n => n.Id == id);
            Require(node is not null && CanRemove(id, node.RequirementIds)); nodes.Remove(node!);
        }
        foreach (var id in patch.RemovedEdgeIds)
        {
            var edge = edges.FirstOrDefault(e => e.Id == id);
            Require(edge is not null && CanRemove(id, edge.RequirementIds)); edges.Remove(edge!);
        }
        if (patch.EdgeOrder.Count > 0)
        {
            Require(issues.Any(i => i.Field == "order") && patch.EdgeOrder.Count == edges.Count &&
                patch.EdgeOrder.Distinct().Count() == edges.Count && patch.EdgeOrder.All(id => edges.Any(e => e.Id == id)));
            var frozen = before.Edges.Where(e => !Affected(e.Id, e.RequirementIds)).Select(e => e.Id).ToArray();
            Require(patch.EdgeOrder.Where(frozen.Contains).SequenceEqual(frozen));
            edges = patch.EdgeOrder.Select(id => edges.Single(e => e.Id == id)).ToList();
        }
        var result = before with { Nodes = nodes, Edges = edges };
        Require(NaturalDesignValidation.BasicIssues(result).Count == 0);
        return result;

        bool CanRemove(string id, IReadOnlyList<string> refs) => Whole() || issues.Any(i =>
            (i.ItemId == id || i.RelatedElementIds?.Contains(id) == true || refs.Contains(i.ItemId)) &&
            (i.Code == "NaturalUnsupportedClaim" || i.Field == "structure"));
    }
}
