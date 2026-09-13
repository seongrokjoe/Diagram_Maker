using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class ExecutionMeaningValidation
{
    public const string Version = "execution-meaning-v1";
    public static IReadOnlyList<ExecutionMeaningStep> Steps(IReadOnlyList<ExecutionFact> events) =>
        ExecutionSequenceProjection.Flatten(events).Select(e => new ExecutionMeaningStep(e.Id, e.Kind, e.Expression, e.Value,
            e.Evaluation.Select(i => i.Id).ToArray(), e.Children.Select(i => i.Id).ToArray(), e.Alternative.Select(i => i.Id).ToArray(), e.TerminationTarget)).ToArray();

    public static string? Check(ExecutionMeaningInput input, ExecutionMeaningPlan plan)
    {
        bool Text(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length &&
            Regex.IsMatch(value, "[가-힣]") && !Regex.IsMatch(value, "[<>`]");
        if (plan is null || !Text(plan.Summary, 500) || plan.Basis is not ("implementation" or "name-only" or "insufficient")) return "ExecutionPlanRoleInvalid";
        var expected = Steps(input.Events);
        if (plan.Steps is null || plan.Steps.Any(e => e is null) || !plan.Steps.Select(e => e.Id).SequenceEqual(expected.Select(e => e.Id)))
            return "ExecutionPlanEventOrderMismatch";
        for (var i = 0; i < expected.Count; i++)
        {
            var actual = plan.Steps[i]; var fact = expected[i];
            if (actual.Kind != fact.Kind || actual.Expression != fact.Expression || actual.Value != fact.Value || actual.TerminationTarget != fact.TerminationTarget ||
                actual.EvaluationIds is null || !actual.EvaluationIds.SequenceEqual(fact.EvaluationIds) ||
                actual.ChildIds is null || !actual.ChildIds.SequenceEqual(fact.ChildIds) ||
                actual.AlternativeIds is null || !actual.AlternativeIds.SequenceEqual(fact.AlternativeIds)) return "ExecutionPlanBehaviorMismatch";
        }
        if (plan.Units is null || plan.Units.Any(u => u is null || !Text(u.Summary, 80) || !Text(u.Description, 500) || u.EventIds is null || u.EventIds.Count == 0))
            return "ExecutionPlanUnitsInvalid";
        var ids = plan.Units.SelectMany(u => u.EventIds).ToArray();
        if (ids.Length != expected.Count || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(expected.Select(e => e.Id)))
            return "ExecutionPlanCoverageMismatch";
        // Grouping can only combine adjacent siblings. It cannot cross a call,
        // decision, loop or termination, or move work across a branch boundary.
        var regions = new Dictionary<string, (string Region, int Index, string Kind)>();
        void Index(IReadOnlyList<ExecutionFact> events, string region)
        {
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i]; regions[e.Id] = (region, i, e.Kind);
                Index(e.Evaluation, e.Id + "/evaluation"); Index(e.Children, e.Id + "/true"); Index(e.Alternative, e.Id + "/false");
            }
        }
        Index(input.Events, input.Id);
        foreach (var unit in plan.Units.Where(u => u.EventIds.Count > 1))
        {
            var members = unit.EventIds.Select(id => regions[id]).ToArray();
            if (members.Any(m => m.Region != members[0].Region || m.Kind is not ("declare" or "assign")) ||
                !members.Select(m => m.Index).SequenceEqual(Enumerable.Range(members[0].Index, members.Length)))
                return "ExecutionPlanCrossesControlBoundary";
        }
        return null;
    }
}
