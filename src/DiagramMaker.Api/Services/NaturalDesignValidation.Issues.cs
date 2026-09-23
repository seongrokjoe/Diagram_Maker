using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static partial class NaturalDesignValidation
{
    public static IReadOnlyList<NaturalIssue> DesignIssues(NaturalDesign design, string type, NaturalRequirements requirements)
    {
        if (design.Nodes is null || design.Edges is null || design.Notes is null)
            return [new("design", "structure", "NaturalDesignFieldsInvalid", "Return non-null concepts, connections and notes.")];
        var known = requirements.Requirements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var assumptions = requirements.Requirements.Where(item => item.Origin == "assumption")
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var findings = new List<NaturalIssue>();
        var nodeKinds = type switch {
            "class" => new[] { "class", "interface" },
            "sequence" => ["participant"],
            "state" => ["state", "initial", "final"],
            _ => ["operation", "decision", "terminal", "component"]
        };
        for (var index = 0; index < design.Nodes.Count; index++)
        {
            var node = design.Nodes[index];
            var id = node?.Id ?? "node";
            void Add(string field, string code, string instruction) =>
                findings.Add(new(id, field, code, instruction, TargetKind: "node", ItemIndex: index));
            if (node is null || node.RequirementIds is null || node.Members is null || node.Details is null)
            {
                Add("structure", "NaturalNodeInvalid", "Return all required fields for this concept.");
                continue;
            }
            if (!nodeKinds.Contains(node.Kind))
                Add("kind", "NaturalNodeInvalid", "Use a concept kind supported by this diagram type.");
            if (node.Shape is not ("" or "decision" or "terminal" or "call" or "process") ||
                type != "flowchart" && node.Shape != "")
                Add("shape", "NaturalNodeInvalid", "Use only the shape allowed by this diagram type.");
            if (node.RequirementIds.Any(requirementId => !known.Contains(requirementId)))
                Add("requirementIds", "NaturalNodeInvalid", "Remove unknown requirement IDs from this concept.");
            if (!node.Assumption && node.RequirementIds.Count == 0)
                Add("requirementIds", "NaturalNodeInvalid", "Cite the requirement that this explicit concept implements.");
            if (!node.Assumption && node.RequirementIds.Any(assumptions.Contains))
                Add("assumption", "NaturalNodeInvalid", "Mark this proposed concept as an assumption.");
            if (type == "class" && node.Members.Count == 0)
                Add("members", "NaturalClassMembersMissing", "Provide grounded or marked-assumption fields and methods for this class.");
        }
        var edgeKinds = type switch {
            "class" => new[] { "inherits", "implements", "association", "depends", "aggregation", "composition" },
            "sequence" => ["message", "response"], "state" => ["transition"], _ => ["flow"]
        };
        for (var index = 0; index < design.Edges.Count; index++)
        {
            var edge = design.Edges[index];
            var id = edge?.Id ?? "edge";
            void Add(string field, string code, string instruction) =>
                findings.Add(new(id, field, code, instruction, TargetKind: "edge", ItemIndex: index));
            if (edge is null || edge.RequirementIds is null || edge.ControlPath is null)
            {
                Add("structure", "NaturalEdgeInvalid", "Return all required fields for this connection.");
                continue;
            }
            if (!edgeKinds.Contains(edge.Type))
                Add("type", type == "class" ? "NaturalClassRelationInvalid" : "NaturalEdgeInvalid",
                    "Use a connection type supported by this diagram type.");
            if (edge.RequirementIds.Any(requirementId => !known.Contains(requirementId)))
                Add("requirementIds", "NaturalEdgeInvalid", "Remove unknown requirement IDs from this connection.");
            if (!edge.Assumption && edge.RequirementIds.Count == 0)
                Add("requirementIds", "NaturalEdgeInvalid", "Cite the requirement that this explicit connection implements.");
            if (!edge.Assumption && edge.RequirementIds.Any(assumptions.Contains))
                Add("assumption", "NaturalEdgeInvalid", "Mark this proposed connection as an assumption.");
            if (edge.ControlPath.Count > 32 || edge.ControlPath.Any(control => control is null ||
                control.Kind is not ("alt" or "opt" or "loop") || string.IsNullOrWhiteSpace(control.Id)))
                Add("controlPath", "NaturalEdgeInvalid", "Correct the conditional control path on this connection.");
        }
        if (findings.Count > 0) return findings.Take(30).ToArray();
        var mapped = design.Nodes.SelectMany(node => node.RequirementIds)
            .Concat(design.Edges.SelectMany(edge => edge.RequirementIds)).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in requirements.Requirements.Where(item => !mapped.Contains(item.Id)))
            findings.Add(new(requirement.Id, "requirementIds", "NaturalRequirementCoverageMissing",
                "Implement this requirement in actual structure and cite it on the implementing concept or connection.",
                NaturalRequirementEvidence.RangeIds(requirement), TargetKind: "requirement"));
        if (findings.Count > 0) return findings.Take(30).ToArray();
        var error = Design(design, type, requirements);
        if (error is null) return [];
        if (error == "NaturalInterlockMissing")
        {
            var requirement = requirements.Requirements.First(item => item.Kind == "interlock" && !HasInterlock(item.Id));
            return [new(requirement.Id, type switch {
                "class" => "members", "state" => "guard", "sequence" => "controlPath", _ => "shape" },
                error, "Represent this requirement as a blocking conditional path in the selected diagram type.",
                NaturalRequirementEvidence.RangeIds(requirement), TargetKind: "requirement")];
        }
        var boundary = error switch {
            "NaturalInitialIncomingInvalid" => design.Nodes.FirstOrDefault(node => node.Kind == "initial" &&
                design.Edges.Any(edge => edge.TargetId == node.Id)),
            "NaturalInitialOutgoingInvalid" => design.Nodes.FirstOrDefault(node => node.Kind == "initial" &&
                design.Edges.Count(edge => edge.SourceId == node.Id) != 1),
            "NaturalFinalOutgoingInvalid" => design.Nodes.FirstOrDefault(node => node.Kind == "final" &&
                design.Edges.Any(edge => edge.SourceId == node.Id)),
            _ => null
        };
        if (boundary is not null)
            return [new(boundary.Id, "structure", error, "Correct the incoming or outgoing boundary transitions.",
                TargetKind: "node", ItemIndex: Array.IndexOf(design.Nodes.ToArray(), boundary))];
        return [new("design", "structure", error,
            "Correct this structural rule while preserving source conditions and valid elements.")];

        bool HasInterlock(string id) => type switch {
            "class" => design.Nodes.Any(node => node.RequirementIds.Contains(id) &&
                node.Members.Any(member => member.Kind == "method" && member.Preconditions.Count > 0)),
            "state" => design.Edges.Any(edge => edge.RequirementIds.Contains(id) && !string.IsNullOrWhiteSpace(edge.Guard)),
            "sequence" => design.Edges.Any(edge => edge.RequirementIds.Contains(id) &&
                edge.ControlPath.Any(control => control.Kind is "alt" or "opt" && !string.IsNullOrWhiteSpace(control.Label))),
            _ => design.Nodes.Any(node => node.RequirementIds.Contains(id) &&
                (node.Shape == "decision" || node.Kind == "decision") &&
                design.Edges.Count(edge => edge.SourceId == node.Id) >= 2)
        };
    }
}