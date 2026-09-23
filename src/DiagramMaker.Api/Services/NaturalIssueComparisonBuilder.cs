using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class NaturalIssueComparisonBuilder
{
    public static NaturalIssueComparison Build(string diagnosticId, NaturalIssue issue,
        NaturalRequirements requirements, NaturalDesign? design)
    {
        var masker = new SecretMasker();
        (string Value, bool Truncated) Safe(string? value)
        {
            var text = masker.Mask(value ?? "");
            return text.Length <= 2000 ? (text, false) : (text[..2000], true);
        }
        var ranges = requirements.SourceRanges ?? [];
        var cited = (issue.EvidenceIds ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in requirements.Requirements.Where(item => cited.Contains(item.Id) ||
            item.Id == issue.ItemId))
            foreach (var id in NaturalRequirementEvidence.RangeIds(requirement)) cited.Add(id);
        var excerpts = ranges.Where(range => cited.Contains(range.Id)).Take(6).Select(range => {
            var safe = Safe(range.Text);
            return new NaturalComparisonExcerpt(range.Id, safe.Value, safe.Truncated);
        }).ToArray();
        var node = design?.Nodes.FirstOrDefault(item => item.Id == issue.ItemId);
        var edge = design?.Edges.FirstOrDefault(item => item.Id == issue.ItemId);
        var requirementItem = requirements.Requirements.FirstOrDefault(item => item.Id == issue.ItemId);
        var target = node?.Label ?? edge?.Label ?? requirementItem?.Text ?? issue.TargetKind ?? "구조";
        var observed = node is not null ? issue.Field switch {
            "kind" => node.Kind, "shape" => node.Shape, "requirementIds" => string.Join(", ", node.RequirementIds),
            "assumption" => node.Assumption.ToString(), "members" => string.Join(", ", node.Members.Select(item => item.Name)),
            "details" => string.Join("; ", node.Details), _ => node.Label
        } : edge is not null ? issue.Field switch {
            "sourceId" => edge.SourceId, "targetId" => edge.TargetId, "type" => edge.Type,
            "guard" => edge.Guard, "event" => edge.Event, "action" => edge.Action,
            "controlPath" => string.Join("; ", edge.ControlPath.Select(item => item.Kind + " " + item.Label + " " + item.Branch)),
            "requirementIds" => string.Join(", ", edge.RequirementIds),
            "assumption" => edge.Assumption.ToString(), _ => edge.Label
        } : requirementItem is not null && design is not null ? string.Join("; ",
            design.Nodes.Where(item => item.RequirementIds.Contains(requirementItem.Id)).Select(item => item.Label)
                .Concat(design.Edges.Where(item => item.RequirementIds.Contains(requirementItem.Id))
                    .Select(item => item.Label + " " + string.Join(" ", item.ControlPath.Select(control => control.Label + " " + control.Branch))))) :
            requirementItem?.Text ?? "";
        var observedSafe = Safe(observed);
        return new(diagnosticId, NaturalDesignValidation.DiagnosticCode(issue.Code),
            NaturalDesignValidation.DiagnosticField(issue.Field), issue.TargetKind ?? "review",
            Safe(target).Value, observedSafe.Value, observedSafe.Truncated,
            Safe(issue.SourceQuote).Value, Safe(issue.Instruction).Value, excerpts,
            (issue.RelatedElementIds ?? []).Take(6).Select(id => Safe(design?.Nodes.FirstOrDefault(item => item.Id == id)?.Label ??
                design?.Edges.FirstOrDefault(item => item.Id == id)?.Label ?? "").Value).ToArray());
    }
}