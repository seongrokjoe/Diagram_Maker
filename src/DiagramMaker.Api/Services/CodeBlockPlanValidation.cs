using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// Pasted code permits meaningful action groups, while the Git planner keeps its
// stricter change-oriented abstraction policy. Only topology-proven chains merge.
internal static class CodeBlockPlanValidation
{
    public static string? Validate(DiagramIr candidate, CodeBlockSemanticPlan plan)
    {
        if (plan.Elements is null || plan.Messages is null || string.IsNullOrWhiteSpace(plan.Summary) || plan.Summary.Length > 500)
            return "MissingPlanFields";
        var nodes = candidate.Nodes.ToDictionary(n => n.Id);
        var covered = new HashSet<string>();
        var elementIds = new HashSet<string>();
        foreach (var element in plan.Elements)
        {
            if (element is null || string.IsNullOrWhiteSpace(element.Id) || !elementIds.Add(element.Id) ||
                !NaturalLabel(element.Summary, 80) || element.NodeIds is not { Count: > 0 } || element.FactIds is not { Count: > 0 } ||
                element.Condition is null || element.Outcome is null || element.Condition.Length > 240 || element.Outcome.Length > 240)
                return "InvalidElement";
            foreach (var id in element.NodeIds)
                if (!nodes.ContainsKey(id) || !covered.Add(id)) return "UnknownOrDuplicateNode";
            var values = element.NodeIds.Select(id => nodes[id]).ToArray();
            var facts = values.SelectMany(n => n.SourceFactIds ?? []).ToHashSet();
            if (!facts.SetEquals(element.FactIds) || values.Any(n => n.EvidenceIds.Count == 0)) return "MissingElementEvidence";
            if (values.Any(n => n.Kind is "condition" or "loop") && !NaturalLabel(element.Condition, 240)) return "MissingNaturalCondition";
            if (values.Any(n => n.Kind is not ("condition" or "loop" or "entry" or "exit")) && !NaturalLabel(element.Outcome, 240))
                return "MissingOutcome";
            if (element.NodeIds.Count == 1) continue;
            if (candidate.Type != "flowchart" || values.Select(n => n.Group).Distinct().Count() != 1 ||
                values.Select(n => n.DetailPageId).Distinct().Count() != 1 ||
                values.Any(n => n.Group is null || n.Context is null || n.Kind is not ("operation" or "call" or "return")) ||
                values.Take(values.Length - 1).Any(n => n.Kind == "return")) return "UnsafeAbstraction";
            if (values.Select(n => Scope(n.Context!)).Distinct().Count() != 1) return "CrossesControlBoundary";
            // Cross-function call edges must keep their own call site and evidence.
            if (candidate.Edges.Any(e => e.Type == "calls" && element.NodeIds.Contains(e.SourceId))) return "MergesProvenCall";
            for (var i = 1; i < values.Length; i++)
            {
                var previous = values[i - 1]; var current = values[i];
                if (candidate.Edges.Count(e => e.SourceId == previous.Id) != 1 ||
                    candidate.Edges.Count(e => e.TargetId == current.Id) != 1 ||
                    !candidate.Edges.Any(e => e.SourceId == previous.Id && e.TargetId == current.Id && e.Type != "loopBack") ||
                    previous.Context!.Span?.FilePath != current.Context!.Span?.FilePath ||
                    previous.Context.Span?.StartOffset > current.Context.Span?.StartOffset)
                    return "CrossesControlBoundary";
            }
        }
        if (covered.Count != nodes.Count) return "MissingNodeCoverage";
        var edges = candidate.Edges.Select(e => e.Id).ToHashSet();
        if (plan.Messages.Any(m => m is null || !edges.Contains(m.EdgeId) || !NaturalLabel(m.Summary, 120)) ||
            plan.Messages.Select(m => m.EdgeId).Distinct().Count() != plan.Messages.Count) return "InvalidMessages";
        if (candidate.Type is "sequence" or "state" && plan.Messages.Count != edges.Count) return "MissingMessageCoverage";
        var controls = ControlBlocks(candidate.SequenceBlocks ?? []).ToArray();
        if ((plan.Controls ?? []).Any(c => c is null || !NaturalLabel(c.Label, 120)) ||
            !(plan.Controls ?? []).Select(c => c.Id).ToHashSet().SetEquals(controls.Select(c => c.Id)) ||
            (plan.Controls?.Count ?? 0) != controls.Length) return "MissingControlCoverage";
        return null;
    }

    public static IEnumerable<SequenceBlock> ControlBlocks(IEnumerable<SequenceBlock> blocks) => blocks.SelectMany(b =>
        (b.Kind is "alt" or "loop" or "break" ? new[] { b } : []).Concat(ControlBlocks(b.Children)));

    private static string Scope(CodeContext context) => string.Join("|", context.ControlPath.Select(s => s.Id + ":" + s.Branch));
    private static bool NaturalLabel(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        Regex.IsMatch(value, "[가-힣]") && !Regex.IsMatch(value, @"```|<\s*/?\s*[a-zA-Z][^>]*>|%%\{|\b(?:javascript|data)\s*:", RegexOptions.IgnoreCase) &&
        value.Trim() is not ("데이터 처리" or "관련 기능 호출" or "코드 처리");
}
