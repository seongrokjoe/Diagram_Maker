using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record NaturalReferenceBinding(string TargetKind, string TargetId, IReadOnlyList<string> RequirementIds);
internal sealed record NaturalReferenceRepair(IReadOnlyList<NaturalReferenceBinding> Bindings);

internal static class NaturalReferenceRecovery
{
    internal static bool IsReferenceIssue(NaturalIssue issue) => issue.Code is
        "NaturalNodeRequirementIdsMissing" or "NaturalNodeRequirementIdsUnknown" or
        "NaturalEdgeRequirementIdsMissing" or "NaturalEdgeRequirementIdsUnknown";

    internal static NaturalDesign CompleteParticipants(NaturalDesign design, string type, NaturalRequirements requirements)
    {
        if (type != "sequence" || design.Nodes is null || design.Edges is null) return design;
        var entities = requirements.Entities.ToHashSet(StringComparer.Ordinal);
        var explicitIds = requirements.Requirements.Where(item => item.Origin == "explicit")
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return design with { Nodes = design.Nodes.Select(node =>
        {
            if (node is null || node.Kind != "participant" || node.Assumption ||
                node.RequirementIds is not { Count: 0 } || !entities.Contains(node.Label)) return node!;
            var incident = design.Edges.Where(edge => edge is not null && !edge.Assumption &&
                edge.RequirementIds is { Count: > 0 } &&
                edge.RequirementIds.All(explicitIds.Contains) &&
                (edge.SourceId == node.Id || edge.TargetId == node.Id))
                .SelectMany(edge => edge.RequirementIds).Distinct(StringComparer.Ordinal).ToArray();
            return incident.Length == 0 ? node : node with { RequirementIds = incident };
        }).ToArray() };
    }

    internal static string? Check(NaturalReferenceRepair repair, IReadOnlyList<NaturalIssue> issues,
        NaturalRequirements requirements, NaturalDesign design)
    {
        var targets = issues.Where(IsReferenceIssue)
            .Select(issue => Key(issue.TargetKind!, issue.ItemId)).ToHashSet(StringComparer.Ordinal);
        if (repair?.Bindings is null || repair.Bindings.Count != targets.Count) return "NaturalReferenceRepairInvalid";
        var known = requirements.Requirements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var explicitIds = requirements.Requirements.Where(item => item.Origin == "explicit")
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in repair.Bindings)
        {
            if (binding is null || binding.TargetKind is not ("node" or "edge") ||
                string.IsNullOrWhiteSpace(binding.TargetId) || binding.RequirementIds is null ||
                !targets.Contains(Key(binding.TargetKind, binding.TargetId)) ||
                !seen.Add(Key(binding.TargetKind, binding.TargetId)) ||
                binding.RequirementIds.Count != binding.RequirementIds.Distinct(StringComparer.Ordinal).Count() ||
                binding.RequirementIds.Any(id => !known.Contains(id)))
                return "NaturalReferenceRepairInvalid";
            var node = design.Nodes.FirstOrDefault(item => item.Id == binding.TargetId);
            var edge = design.Edges.FirstOrDefault(item => item.Id == binding.TargetId);
            if (binding.TargetKind == "node" && node is null || binding.TargetKind == "edge" && edge is null)
                return "NaturalReferenceRepairInvalid";
            var assumption = binding.TargetKind == "node" ? node!.Assumption : edge!.Assumption;
            if (!assumption && binding.RequirementIds.Any(id => !explicitIds.Contains(id)))
                return "NaturalReferenceRepairInvalid";
        }
        return null;
    }

    internal static NaturalDesign Apply(NaturalDesign design, NaturalReferenceRepair repair)
    {
        var bindings = repair.Bindings.ToDictionary(item => Key(item.TargetKind, item.TargetId), StringComparer.Ordinal);
        return design with {
            Nodes = design.Nodes.Select(node => bindings.TryGetValue(Key("node", node.Id), out var binding)
                ? node with { RequirementIds = binding.RequirementIds } : node).ToArray(),
            Edges = design.Edges.Select(edge => bindings.TryGetValue(Key("edge", edge.Id), out var binding)
                ? edge with { RequirementIds = binding.RequirementIds } : edge).ToArray()
        };
    }

    private static string Key(string kind, string id) => kind + "\u001f" + id;
}
