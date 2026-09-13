using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class ExecutionStateProjection
{
    public static IReadOnlyList<CodeBlockStateTransition> Extract(string symbolId, IReadOnlyList<ExecutionFact> events)
    {
        var result = new List<CodeBlockStateTransition>();
        void Visit(IReadOnlyList<ExecutionFact> items, Dictionary<string, (string Value, ExecutionFact Guard)> guards, IReadOnlyList<ExecutionFact> path)
        {
            foreach (var item in items)
            {
                if (item.Kind == "branch")
                {
                    Visit(item.Evaluation, guards, path);
                    var positive = new Dictionary<string, (string Value, ExecutionFact Guard)>(guards);
                    if (!item.Expression.Contains("||") && !ExecutionSequenceProjection.Flatten(item.Evaluation).Any(e => e.Kind == "call"))
                        foreach (var term in item.Expression.Split("&&"))
                        {
                            var match = Regex.Match(term.Trim().Trim('(', ')'), @"^\s*([A-Za-z_]\w*)\s*==\s*([\w:.]+)\s*$", RegexOptions.CultureInvariant);
                            if (match.Success) positive[match.Groups[1].Value] = (match.Groups[2].Value, item);
                        }
                    Visit(item.Children, positive, path.Append(item).ToArray());
                    Visit(item.Alternative, new(guards), path.Append(item with { Expression = "!(" + item.Expression + ")" }).ToArray());
                    // Do not transport a state observation over a potentially mutating branch.
                    foreach (var write in ExecutionSequenceProjection.Flatten(item.Children.Concat(item.Alternative)))
                        if (write.Kind is "call" or "unsupported" or "invalidate") guards.Clear();
                        else if (write.Variable is { } variable) guards.Remove(variable);
                }
                else if (item.Kind == "assign" && item.Variable is { } variable)
                {
                    if (guards.TryGetValue(variable, out var from) && item.Value is { } to &&
                        Regex.IsMatch(to, @"^[\w:.]+$", RegexOptions.CultureInvariant) &&
                        (new[] { "state", "status", "phase", "mode" }.Any(n => variable.Contains(n, StringComparison.OrdinalIgnoreCase)) || to.Contains("::") || to.Contains('.')))
                        result.Add(new(StableIds.Create(symbolId, "transition", item.Id), symbolId, variable, from.Value, to,
                            string.Join(" && ", path.Select(p => "(" + p.Expression + ")")),
                            path.SelectMany(p => p.EvidenceIds ?? []).Concat(from.Guard.EvidenceIds ?? []).Concat(item.EvidenceIds ?? []).Distinct().ToArray()));
                    guards.Remove(variable);
                }
                else if (item.Kind == "declare" && item.Variable is { } local) guards.Remove(local);
                else if (item.Kind is "return" or "throw" or "break" or "continue") return;
                else if (item.Kind is "call" or "invalidate" or "unsupported" or "unordered") guards.Clear();
                else if (item.Kind == "loop") { Visit(item.Children, [], path); guards.Clear(); }
            }
        }
        Visit(events, [], []);
        return result;
    }

    public static DiagramIr Build(string title, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected, string direction)
    {
        var transitions = (graph.Executions ?? []).Where(m => selected.Contains(m.IdentityId)).SelectMany(method =>
            Extract(method.IdentityId, method.Events).Select(t => (Transition: t, Method: method))).ToArray();
        var nodes = new Dictionary<string, DiagramNode>();
        var edges = new List<DiagramEdge>();
        string StateId(string owner, string variable, string state) => StableIds.Create("git-state", owner, variable, state);
        foreach (var matching in transitions.GroupBy(t => (t.Method.IdentityId, t.Transition.Variable, t.Transition.From, t.Transition.To)))
        {
            var before = matching.Where(t => t.Method.RevisionSha == comparison.BaseSha).ToArray();
            var after = matching.Where(t => t.Method.RevisionSha == comparison.TargetSha).ToArray();
            var paired = new HashSet<string>();
            foreach (var current in after)
            {
                var identical = before.Where(t => !paired.Contains(t.Transition.Id) && t.Transition.Condition == current.Transition.Condition).ToArray();
                var previous = identical.Length == 1 ? identical : before.Length == 1 && after.Length == 1 ? before : [];
                if (previous.Length == 1) paired.Add(previous[0].Transition.Id);
                Add(current.Transition, current.Method, previous.Length == 0 ? "added" :
                    previous[0].Transition.Condition == current.Transition.Condition ? "unchanged" : "modified",
                    previous.Length == 1 ? previous[0].Transition : null);
            }
            foreach (var old in before.Where(t => !paired.Contains(t.Transition.Id))) Add(old.Transition, old.Method, "deleted", null);
        }
        return new("state", title, nodes.Values.ToArray(), edges, ["동일 변수의 조건과 대입이 확인된 전이입니다. 초기·종료 상태는 추정하지 않습니다."],
            ["git", SourceGraphAnalyzer.IndexVersion, "execution-state-v1"], direction);

        void Add(CodeBlockStateTransition transition, MethodExecution method, string status, CodeBlockStateTransition? previous)
        {
            var evidence = transition.EvidenceIds.Concat(previous?.EvidenceIds ?? []).Distinct().ToArray();
            var original = graph.Evidence.FirstOrDefault(e => transition.EvidenceIds.Contains(e.Id));
            var marker = status == "unchanged" ? null : new DiagramChangeMarker(status switch
            { "added" => DiagramChangeKind.Added, "deleted" => DiagramChangeKind.Deleted, _ => DiagramChangeKind.Modified },
                DiagramChangePrecision.Exact, method.FilePath, original?.StartLine, original?.EndLine, evidence);
            var version = graph.Versions.First(v => v.IdentityId == method.IdentityId && v.RevisionSha == method.RevisionSha);
            var facts = (graph.Executions ?? []).SelectMany(m => ExecutionSequenceProjection.Flatten(m.Events))
                .Where(e => (e.EvidenceIds ?? []).Any(evidence.Contains)).Select(e => e.Id).ToArray();
            foreach (var value in new[] { transition.From, transition.To })
            {
                var id = StateId(method.IdentityId, transition.Variable, value);
                nodes[id] = new(id, value, "state", version.QualifiedName + " / " + transition.Variable, "unchanged", Confidence.Exact,
                    evidence.Concat(nodes.GetValueOrDefault(id)?.EvidenceIds ?? []).Distinct().ToArray(),
                    SourceFactIds: facts.Concat(nodes.GetValueOrDefault(id)?.SourceFactIds ?? []).Distinct().ToArray());
            }
            edges.Add(new(StableIds.Create(method.RevisionSha, transition.Id), StateId(method.IdentityId, transition.Variable, transition.From), StateId(method.IdentityId, transition.Variable, transition.To),
                "transition", status == "modified" ? $"{previous!.Condition} → {transition.Condition}" : transition.Condition,
                status, Confidence.Exact, evidence, ChangeMarker: marker, SourceFactIds: facts, RelationOrigin: "code", OriginalExpression: transition.Condition));
        }
    }
}
