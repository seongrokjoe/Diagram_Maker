using System.Text.RegularExpressions;
using DiagramMaker.Domain;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagramMaker.Services;

public static class ExecutionSequenceProjection
{
    public static IEnumerable<ExecutionFact> Flatten(IEnumerable<ExecutionFact> events) => events.SelectMany(e =>
        new[] { e }.Concat(Flatten(e.Evaluation)).Concat(Flatten(e.Children)).Concat(Flatten(e.Alternative)));

    private static HashSet<string> Exits(IReadOnlyList<ExecutionFact> events)
    {
        var exits = new HashSet<string> { "next" };
        foreach (var fact in events)
        {
            if (!exits.Remove("next")) break;
            exits.UnionWith(fact.Kind is "return" or "throw" or "break" or "continue" ? [fact.Kind] :
                fact.Kind == "branch" ? Exits(fact.Children).Union(Exits(fact.Alternative)) : ["next"]);
        }
        return exits;
    }

    public static DiagramIr Build(CodeBlockSymbol symbol, CodeBlockGraph graph, string direction)
    {
        var nodes = new Dictionary<string, DiagramNode>();
        var edges = new List<DiagramEdge>();
        var notes = new List<string> { "한 함수의 가능한 실행 경로이며 실제 실행 기록이 아닙니다. 호출 대상의 내부 구현은 상세 페이지에서 확인하세요." };
        var caller = StableIds.Create(symbol.Id, "caller");
        nodes[symbol.Id] = Participant(symbol.Id, symbol.Name, symbol.EvidenceIds, [symbol.Id]);
        nodes[caller] = Participant(caller, "호출자", [], []);
        var replacements = new Dictionary<string, string>();
        var escaped = new HashSet<string>();
        var locals = new HashSet<string>();
        var callIndex = 0;
        var executionFacts = Flatten(symbol.Execution ?? []).ToDictionary(f => f.Id);
        string Condition(string expression)
        {
            foreach (var (original, value) in replacements.OrderByDescending(p => p.Key.Length))
                expression = expression.Replace(original, value, StringComparison.Ordinal);
            return expression;
        }
        string Value(string? expression, Dictionary<string, string> values)
        {
            expression = expression?.Trim();
            if (expression is null) return "unknown";
            if (Regex.IsMatch(expression, "^(true|false|null|nullptr|void|-?[0-9]+(?:\\.[0-9]+)?)$", RegexOptions.CultureInvariant)) return expression;
            return values.GetValueOrDefault(expression, "unknown");
        }
        SequenceBlock Message(ExecutionFact fact, string type, string from, string to, string label, string? value = null, CallPresentation? call = null)
        {
            var id = StableIds.Create(symbol.Id, fact.Id, type);
            edges.Add(new DiagramEdge(id, from, to, type, label, "unchanged", Confidence.Exact, fact.EvidenceIds ?? [],
                edges.Count + 1, SourceFactIds: new[] { fact.Id, fact.CallSiteId }.OfType<string>().ToArray(), RelationOrigin: "code",
                OriginalExpression: fact.Expression, ReturnValue: value, TerminationTarget: fact.TerminationTarget, Call: call,
                Context: call is null ? null : new CodeContext(fact.Expression, call.Target, null, call.Arguments, call.AssignedTo, null, [],
                    symbol.Calls.FirstOrDefault(c => c.Id == fact.CallSiteId)?.ControlPath ?? [],
                    new("code-block", "", symbol.BlockId, symbol.Location.StartLine, symbol.Location.EndLine, fact.StartOffset, fact.EndOffset), "call")));
            return new(id, "message", label, [], id);
        }
        SequenceBlock Block(ExecutionFact fact, string kind, string label, IReadOnlyList<SequenceBlock> children) =>
            new(fact.Id, kind, label, children, ParticipantIds: [symbol.Id], EvidenceIds: fact.EvidenceIds,
                SourceFactIds: [fact.Id], OriginalExpression: fact.Expression, TerminationTarget: fact.TerminationTarget);
        (IReadOnlyList<SequenceBlock> Blocks, bool Terminated) Visit(IReadOnlyList<ExecutionFact> events, Dictionary<string, string> values)
        {
            var result = new List<SequenceBlock>();
            for (var index = 0; index < events.Count; index++)
            {
                var fact = events[index];
                if (fact.Kind == "call")
                {
                    var call = symbol.Calls.FirstOrDefault(c => c.Id == fact.CallSiteId);
                    var target = graph.Symbols.FirstOrDefault(s => s.Id == call?.TargetSymbolId);
                    var presentation = CallPresentationBuilder.Build(fact, call, target, symbol, graph);
                    var boundary = StableIds.Create(symbol.Id, "unresolved", presentation.Target);
                    var targetId = target?.Id ?? boundary;
                    nodes.TryAdd(targetId, target is null ? Participant(boundary, presentation.Target, [], []) with
                        { Details = [presentation.Basis == "api-contract" ? "외부 API · 공식 계약" : "호출 대상 · 구현 미확인", call?.ResolutionReason ?? "targetNotProven"] } :
                        Participant(target.Id, target.Name, target.EvidenceIds, [target.Id]) with { DetailPageId = CodeBlockProjectionService.Detail(target.Id) });
                    result.Add(Message(fact, "message", symbol.Id, targetId, fact.Expression, call: presentation));
                    var response = "r" + ++callIndex;
                    if (fact.Value != "discarded")
                    {
                        result.Add(Message(fact, "response", targetId, symbol.Id,
                            CallPresentationBuilder.ReturnLabel(presentation, response + " · 반환값") + OutputLabel(presentation), "unknown", presentation));
                        replacements[fact.Expression] = presentation.AssignedTo ?? response;
                    }
                    else if (presentation.Outputs.Count > 0)
                        result.Add(Message(fact, "response", targetId, symbol.Id, OutputLabel(presentation).TrimStart(' ', '·'), call: presentation));
                    foreach (var variable in values.Keys.ToArray())
                        if (Regex.IsMatch(fact.Expression, $@"(?:\bref\s+|\bout\s+|&\s*){Regex.Escape(variable)}\b"))
                        { values.Remove(variable); escaped.Add(variable); }
                }
                else if (fact.Kind is "declare" or "assign")
                {
                    if (fact.Kind == "declare" && fact.Variable is { } declared) locals.Add(declared);
                    var value = Value(fact.Value, values);
                    if (fact.Variable is null)
                    { escaped.UnionWith(values.Keys); values.Clear(); }
                    else if (locals.Contains(fact.Variable) && !escaped.Contains(fact.Variable))
                    {
                        if (value == "unknown") values.Remove(fact.Variable);
                        else values[fact.Variable] = value;
                    }
                    var (assignedValue, conversion) = UnwrapAssignment(fact.Value);
                    var localMessages = result.Where(b => b.Kind == "message").Select(b => b.EdgeId).ToHashSet();
                    var responseEdge = fact.Variable is null ? null : edges.LastOrDefault(e => localMessages.Contains(e.Id) &&
                        e.Type == "response" && e.Call is not null && e.OriginalExpression == assignedValue &&
                        (e.SourceFactIds ?? []).Any(id => executionFacts.TryGetValue(id, out var callFact) && callFact.Kind == "call" &&
                            callFact.StartOffset >= fact.StartOffset && callFact.EndOffset <= fact.EndOffset));
                    if (responseEdge is not null)
                    {
                        var position = edges.IndexOf(responseEdge);
                        var presentation = responseEdge.Call! with { AssignedTo = fact.Variable, ReturnType = fact.ValueType ?? responseEdge.Call!.ReturnType };
                        edges[position] = responseEdge with { Call = presentation,
                            Label = CallPresentationBuilder.ReturnLabel(presentation, "반환값") + (conversion ? " · 형 변환" : "") + OutputLabel(presentation),
                            EvidenceIds = responseEdge.EvidenceIds.Concat(fact.EvidenceIds ?? []).Distinct().ToArray(),
                            SourceFactIds = (responseEdge.SourceFactIds ?? []).Append(fact.Id).Distinct().ToArray() };
                        replacements[responseEdge.OriginalExpression!] = fact.Variable!;
                    }
                    else if (fact.Kind != "declare" || fact.Value is not null)
                        result.Add(Block(fact, "note", AssignmentSummary(fact), []));
                }
                else if (fact.Kind is "return" or "throw")
                {
                    var value = Value(fact.Value, values);
                    var label = fact.Kind == "throw" ? "예외 전달: " + fact.Value : value == "unknown"
                        ? $"반환 {Condition(fact.Value ?? "값")}" : $"반환 {value}";
                    result.Add(Message(fact, fact.Kind, symbol.Id, caller, label, value));
                    return (result, true);
                }
                else if (fact.Kind == "branch")
                {
                    result.AddRange(Visit(fact.Evaluation, values).Blocks);
                    var whenTrue = new Dictionary<string, string>(values);
                    var whenFalse = new Dictionary<string, string>(values);
                    var yes = Visit(fact.Children, whenTrue);
                    var no = Visit(fact.Alternative, whenFalse);
                    var label = Condition(fact.Expression);
                    // A guard ends this function; the remainder occurs exactly once,
                    // outside the break fragment. No accumulated false-path nesting.
                    if (yes.Terminated && fact.Alternative.Count == 0 &&
                        Flatten(fact.Children).All(e => e.Kind is not ("break" or "continue")))
                        result.Add(Block(fact, "break", label + " · 참이면 함수 종료", yes.Blocks) with { TerminationTarget = "function" });
                    else if (no.Terminated && fact.Children.Count == 0 &&
                        Flatten(fact.Alternative).All(e => e.Kind is not ("break" or "continue")))
                        result.Add(Block(fact, "break", "!(" + label + ") · 참이면 함수 종료", no.Blocks) with { TerminationTarget = "function" });
                    else result.Add(Block(fact, "alt", label, [new(fact.Id + "_true", "branch", "then", yes.Blocks),
                        new(fact.Id + "_false", "branch", "else", no.Blocks)]));
                    values.Clear();
                    var survivors = yes.Terminated ? whenFalse : no.Terminated ? whenTrue :
                        whenTrue.Where(p => whenFalse.GetValueOrDefault(p.Key) == p.Value).ToDictionary();
                    foreach (var pair in survivors) values[pair.Key] = pair.Value;
                    if (yes.Terminated && no.Terminated) return (result, true);
                    if (Exits([fact]).Any(e => e is "break" or "continue") && index + 1 < events.Count)
                    {
                        var remaining = Visit(events.Skip(index + 1).ToArray(), values);
                        result.Add(Block(fact, "opt", "앞선 반복 이동 없이 본문을 계속하는 경로", remaining.Blocks) with { Id = fact.Id + "_remainder" });
                        return (result, false);
                    }
                }
                else if (fact.Kind == "loop")
                {
                    // A loop-carried value cannot be treated as its entry constant.
                    foreach (var write in Flatten([fact]).Where(e => e.Kind is "assign" or "invalidate"))
                        if (write.Variable is { } variable) values.Remove(variable); else values.Clear();
                    var local = new Dictionary<string, string>(values);
                    var body = new List<SequenceBlock>();
                    if (fact.Value is not ("post-test" or "foreach")) body.AddRange(Visit(fact.Evaluation, local).Blocks);
                    var iteration = Visit(fact.Children, local).Blocks.ToList();
                    var exits = Exits(fact.Children);
                    if (exits.Contains("next") || exits.Contains("continue"))
                    {
                        var tail = Visit(fact.Alternative, local).Blocks.ToList();
                        if (fact.Value == "post-test") tail.AddRange(Visit(fact.Evaluation, local).Blocks);
                        if (tail.Count > 0)
                            if (exits.Any(e => e is "return" or "throw" or "break"))
                                iteration.Add(Block(fact, "opt", "반복·함수 종료 경로 제외 (continue 포함)", tail) with { Id = fact.Id + "_update" });
                            else iteration.AddRange(tail);
                    }
                    if (fact.Value is "post-test" or "foreach") body.AddRange(iteration);
                    else body.Add(Block(fact, "alt", Condition(fact.Expression),
                        [new(fact.Id + "_body", "branch", "then", iteration),
                         new(fact.Id + "_end", "branch", "else", [Block(fact, "note", "조건 거짓: 현재 반복 종료", []) with { Id = fact.Id + "_stop", TerminationTarget = fact.Id }])]) with { Id = fact.Id + "_test" });
                    result.Add(Block(fact, "loop", (fact.Value == "post-test" ? "본문 후 조건 평가: " : fact.Value == "foreach" ? "각 원소: " : "조건 평가 반복: ") + fact.Expression, body));
                    values.Clear();
                }
                else if (fact.Kind is "break" or "continue")
                {
                    result.Add(Block(fact, "note", fact.Kind == "break" ? "현재 반복 종료 (나머지 본문·증감·조건 평가 생략)" : "나머지 본문 생략 → 증감·조건 평가로 이동", []));
                    return (result, true);
                }
                else if (fact.Kind is "unordered" or "region")
                {
                    result.Add(Block(fact, fact.Kind == "unordered" ? "unordered" : "sequence", fact.Expression, Visit(fact.Children, values).Blocks));
                    if (fact.Kind == "unordered") values.Clear();
                }
                else if (fact.Kind == "unsupported")
                {
                    values.Clear();
                    var observed = fact.Children.Select(e => Block(e, "sequence", "순서·실행 조건 미확인", Visit([e], []).Blocks) with { Id = e.Id + "_unscoped" }).ToArray();
                    result.Add(Block(fact, "unordered", fact.Expression, observed));
                    notes.Add(fact.Expression);
                }
                else
                {
                    if (fact.Kind == "invalidate" && fact.Variable is { } variable)
                    { values.Remove(variable); if (fact.Value == "escaped") escaped.Add(variable); }
                    else values.Clear();
                    if (!(fact.Kind == "invalidate" && fact.Value == "escaped"))
                        result.Add(Block(fact, "note", AssignmentSummary(fact), []));
                    if (fact.Kind == "unsupported") notes.Add(fact.Expression);
                }
            }
            return (result, false);
        }
        var blocks = SummarizeNotes(Visit(symbol.Execution ?? [], []).Blocks);
        return new("sequence", symbol.Name, nodes.Values.ToArray(), edges, notes.Distinct().ToArray(),
            ["code-block", graph.AnalyzerVersion, "execution-facts-v1", DiagramPresentation.Version], direction,
            [new SequenceBlock(StableIds.Create("scenario", symbol.Id), "scenario", symbol.Name, blocks)]);
    }
    private static DiagramNode Participant(string id, string label, IReadOnlyList<string> evidence, IReadOnlyList<string> facts) =>
        new(id, label, "participant", null, "unchanged", Confidence.Exact, evidence, SourceFactIds: facts);

    private static (string? Expression, bool Conversion) UnwrapAssignment(string? value)
    {
        if (value is null) return (null, false);
        var expression = SyntaxFactory.ParseExpression(value);
        var conversion = false;
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            else if (expression is CastExpressionSyntax cast) { conversion = true; expression = cast.Expression; }
            else if (expression is InvocationExpressionSyntax { Expression: GenericNameSyntax name, ArgumentList.Arguments.Count: 1 } invocation &&
                name.Identifier.ValueText is "static_cast" or "dynamic_cast" or "reinterpret_cast" or "const_cast")
            { conversion = true; expression = invocation.ArgumentList.Arguments[0].Expression; }
            else break;
        }
        if (!expression.ContainsDiagnostics) return (expression.ToString(), conversion);
        var cppCast = Regex.Match(expression.ToString().Trim(), @"^(?:static_cast|dynamic_cast|reinterpret_cast|const_cast)<[^<>]+>\((?<value>.*)\)$", RegexOptions.Singleline);
        return cppCast.Success ? (cppCast.Groups["value"].Value.Trim(), true) : (value.Trim(), false);
    }

    private static string AssignmentSummary(ExecutionFact fact)
    {
        var name = fact.Variable;
        if (name is null)
        {
            var target = Regex.Match(fact.Expression, @"^\s*(?:\+\+|--)?\s*(?<target>\*?[\w.]+(?:->\w+)*)");
            name = target.Success ? target.Groups["target"].Value : "내부 값";
        }
        var value = fact.Value?.Trim();
        return value is not null && Regex.IsMatch(value, @"^(?:true|false|null|nullptr|-?\d+(?:\.\d+)?|[\w.]+)$")
            ? $"{name} 값을 {value}로 설정" : $"{name} 값 갱신";
    }

    private static IReadOnlyList<SequenceBlock> SummarizeNotes(IReadOnlyList<SequenceBlock> blocks)
    {
        var result = new List<SequenceBlock>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i] with { Children = SummarizeNotes(blocks[i].Children) };
            if (block.Kind != "note" || block.TerminationTarget is not null) { result.Add(block); continue; }
            var run = new List<SequenceBlock> { block };
            while (i + 1 < blocks.Count && blocks[i + 1].Kind == "note" && blocks[i + 1].TerminationTarget is null &&
                (blocks[i + 1].ParticipantIds ?? []).SequenceEqual(block.ParticipantIds ?? [])) run.Add(blocks[++i]);
            if (run.Count == 1) { result.Add(block); continue; }
            // Original statements remain individually available in execution evidence.
            result.Add(block with { Id = block.Id + "_summary", Label = $"지역 값 준비·갱신 ({run.Count}개 동작)",
                OriginalExpression = null, SourceFactIds = run.SelectMany(b => b.SourceFactIds ?? []).Distinct().ToArray(),
                EvidenceIds = run.SelectMany(b => b.EvidenceIds ?? []).Distinct().ToArray(),
                Children = run.Select(b => b with { Kind = "sequence" }).ToArray() });
        }
        return result;
    }

    private static string OutputLabel(CallPresentation call) => string.Join("", call.Outputs.Select(o =>
        " · " + o.Expression + ": " + (o.Description.Length > 80 ? o.Description[..77] + "…" : o.Description)));
}
