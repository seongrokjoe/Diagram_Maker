using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    public bool SupportsSharedSemantics => true;
    private static readonly JsonElement SharedSchema = ParseSchema("""
        {"type":"object","additionalProperties":false,"properties":{
          "summary":{"type":"string","maxLength":500},"recommendedType":{"type":"string"},
          "items":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "id":{"type":"string"},"summary":{"type":"string","maxLength":80},
            "description":{"type":"string","maxLength":500}},"required":["id","summary","description"]}}
        },"required":["summary","recommendedType","items"]}
        """);

    public async Task<SharedDiagramGroup?> PlanCodeBlockGroupAsync(CodeBlockWorkspaceInput input, CodeBlockGraph graph,
        CodeBlockGroupSelection group, IReadOnlyList<DiagramViewSelection> selections, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return null;
        var sourceFacts = new List<SourceFact>();
        SourceSpan Span(CodeBlockLocation location) => new("code-block", "", location.BlockId,
            location.StartLine, location.EndLine, location.StartOffset, location.EndOffset);
        foreach (var symbol in graph.Symbols.Where(s => group.BlockIds.Contains(s.BlockId)))
        {
            sourceFacts.Add(new(symbol.Id, "symbol", symbol.Signature, [], symbol.EvidenceIds, Span(symbol.Location)));
            foreach (var step in symbol.Steps)
                sourceFacts.Add(new(step.Id, step.Kind, step.Label, [], step.EvidenceIds, Span(step.Location)));
            foreach (var call in symbol.Calls)
                sourceFacts.Add(new(call.Id, "call", call.Statement ?? call.Name, [],
                    graph.Evidence.Where(e => e.Location == call.Location).Select(e => e.Id).ToArray(), Span(call.Location)));
            foreach (var execution in ExecutionSequenceProjection.Flatten(symbol.Execution ?? []))
            {
                var evidence = graph.Evidence.FirstOrDefault(e => (execution.EvidenceIds ?? []).Contains(e.Id));
                sourceFacts.Add(new(execution.Id, "execution-" + execution.Kind, execution.Expression, [], execution.EvidenceIds ?? [],
                    evidence is null ? null : Span(evidence.Location), Content: execution.Expression));
            }
        }
        foreach (var transition in graph.Transitions)
        {
            var locations = graph.Evidence.Where(e => transition.EvidenceIds.Contains(e.Id)).Select(e => e.Location).ToArray();
            var span = locations.Length == 0 ? null : Span(locations[0]) with
                { StartOffset = locations.Min(l => l.StartOffset), EndOffset = locations.Max(l => l.EndOffset),
                    StartLine = locations.Min(l => l.StartLine), EndLine = locations.Max(l => l.EndLine) };
            sourceFacts.Add(new(transition.Id, "state", transition.Condition, [], transition.EvidenceIds, span));
        }
        sourceFacts.AddRange(graph.Relations.Select(r => new SourceFact(r.Id, "relation", r.Description, [], r.EvidenceIds ?? [], null)));
        var facts = sourceFacts.DistinctBy(f => f.Id).ToArray();
        var meanings = BuildExecutionMeanings(graph.Symbols.Where(s => group.BlockIds.Contains(s.BlockId) && s.Execution is { Count: > 0 })
            .Select(s => new ExecutionMeaningInput(s.Id, s.Name, Span(s.Location),
                input.Blocks.Single(b => b.Id == s.BlockId).Code[s.Location.StartOffset..s.Location.EndOffset], s.Execution!)).ToArray());
        var prepared = new SharedSemanticProjection(true, facts, meanings);
        var projection = new CodeBlockProjectionService(new());
        foreach (var selection in selections)
            foreach (var page in projection.Build(graph, graph.Relations, group, selection))
                prepared.Add(new(selection.Id + "/" + page.Id, page.Diagram, selection));
        object Sources(IReadOnlyList<SharedSemanticItem> items)
        {
            var ids = items.SelectMany(i => i.FactIds).ToHashSet();
            var spans = facts.Where(f => ids.Contains(f.Id) && f.Span is not null).Select(f => f.Span!)
                .Concat(items.SelectMany(i => i.Contexts).SelectMany(c => (c.Definitions ?? []).Select(d => d.Span).Append(c.Span)))
                .Distinct().ToArray();
            var excerpts = new List<object>();
            foreach (var block in input.Blocks.Where(b => group.BlockIds.Contains(b.Id)))
            {
                var ranges = spans.Where(s => s.FilePath == block.Id && s.StartOffset.HasValue && s.EndOffset.HasValue)
                    .Select(s => (Start: s.StartOffset!.Value, End: s.EndOffset!.Value)).OrderBy(s => s.Start).ThenByDescending(s => s.End).ToArray();
                for (var i = 0; i < ranges.Length; i++)
                {
                    var start = ranges[i].Start; var end = ranges[i].End;
                    while (i + 1 < ranges.Length && (ranges[i + 1].Start <= end ||
                        end >= 0 && ranges[i + 1].Start <= block.Code.Length &&
                        block.Code.AsSpan(end, ranges[i + 1].Start - end).Trim().IsEmpty))
                        end = Math.Max(end, ranges[++i].End);
                    if (start < 0 || end > block.Code.Length || end < start) throw new DiagramGenerationException("INVALID_SOURCE_SPAN", "원본 코드 위치를 확인할 수 없습니다.");
                    excerpts.Add(new { blockId = block.Id, startOffset = start, endOffset = end, code = block.Code[start..end] });
                }
            }
            return new { blocks = input.Blocks.Where(b => group.BlockIds.Contains(b.Id)).Select(b => new { b.Id, b.Language, b.Title, b.Description }),
                excerpts, owners = spans.Select(span => graph.Symbols.Where(s => s.BlockId == span.FilePath &&
                    s.Location.StartOffset <= span.StartOffset && s.Location.EndOffset >= span.EndOffset)
                    .OrderBy(s => s.Location.EndOffset - s.Location.StartOffset).FirstOrDefault())
                    .OfType<CodeBlockSymbol>().DistinctBy(s => s.Id).Select(s => new
                    { s.Id, s.Name, s.Signature, s.BlockId, s.Location.StartOffset, s.Location.EndOffset }),
                facts = facts.Where(f => ids.Contains(f.Id)).Select(f => new
                { f.Id, f.Kind, blockId = f.Span?.FilePath, start = f.Span?.StartOffset, end = f.Span?.EndOffset,
                    description = f.Span is null ? f.Label : null }) };
        }
        var semantics = await GenerateSharedAsync("code-block", group.Title, prepared.Items.Values.ToArray(), Sources,
            selections, input.EnableThinking, cancellationToken, async value =>
            {
                if (SemanticExecution.Current is { } execution)
                    await execution.ReportSharedProjectionAsync(ApplyExecutionStatus(prepared.Apply(value), meanings, facts));
            });
        return ApplyExecutionStatus(prepared.Apply(semantics), meanings, facts);
    }

    public async Task<SharedDiagramGroup?> PlanGitGroupAsync(EvidenceBundle bundle, IReadOnlyList<SharedDiagramInput> diagrams,
        bool enableThinking, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return null;
        var meanings = BuildExecutionMeanings(bundle.ExecutionInputs ?? []);
        var prepared = new SharedSemanticProjection(false, bundle.Facts, meanings);
        foreach (var diagram in diagrams) prepared.Add(diagram);
        var represented = diagrams.SelectMany(d => d.Diagram.Nodes.SelectMany(n => n.SourceFactIds ?? [])
            .Concat(d.Diagram.Edges.SelectMany(e => e.SourceFactIds ?? []))).ToHashSet();
        prepared.AddChanges(bundle.Facts.Where(f => represented.Contains(f.Id)).SelectMany(f => f.ChangeIds));
        object Sources(IReadOnlyList<SharedSemanticItem> items)
        {
            var ids = items.SelectMany(i => i.FactIds).ToHashSet();
            var changes = items.Where(i => i.Kind == "change").SelectMany(i => i.ChangeIds).ToHashSet();
            // Old and new code for a selected change always travel together.
            var requested = bundle.Facts.Where(f => ids.Contains(f.Id)).ToArray();
            var spans = requested.Select(f => f.Span).OfType<SourceSpan>()
                .Concat(items.SelectMany(i => i.Contexts).SelectMany(c => (c.Definitions ?? []).Select(d => d.Span).Append(c.Span))).ToArray();
            bool Overlaps(SourceSpan? span) => span is not null && spans.Any(s => s.RevisionSha == span.RevisionSha &&
                s.FilePath == span.FilePath && s.StartLine <= span.EndLine && s.EndLine >= span.StartLine);
            var facts = bundle.Facts.Where(f => ids.Contains(f.Id) || f.Kind == "source" &&
                (f.ChangeIds.Any(changes.Contains) || Overlaps(f.Span))).DistinctBy(f => f.Id).ToArray();
            var owners = spans.Select(span => bundle.Facts.Where(f => f.Kind == "symbol" && f.Span is { } s &&
                s.RevisionSha == span.RevisionSha && s.FilePath == span.FilePath && s.StartLine <= span.StartLine && s.EndLine >= span.EndLine)
                .OrderBy(f => f.Span!.EndLine - f.Span.StartLine).FirstOrDefault()).OfType<SourceFact>().DistinctBy(f => f.Id);
            // Evidence IDs, blob IDs and full CodeContext objects stay in the
            // server-owned graph. Repeating those on every source line makes
            // overlapping type/method changes dominate the prompt.
            return new { bundle.BaseSha, bundle.TargetSha, owners = owners.Select(f => new { f.Id, f.Label, f.Span }), facts = facts.Select(f => new
                { f.Id, f.Kind, f.ChangeIds, label = f.Kind == "source" ? null : f.Label,
                    revision = f.Span?.RevisionSha, file = f.Span?.FilePath,
                    startLine = f.Span?.StartLine, endLine = f.Span?.EndLine,
                    content = f.Kind == "source" ? f.Content : null }) };
        }
        var semantics = await GenerateSharedAsync("git", "변경 전후 코드의 동작", prepared.Items.Values.ToArray(), Sources,
            diagrams.Select(d => d.Selection).Distinct().ToArray(), enableThinking, cancellationToken, async value =>
            {
                if (SemanticExecution.Current is { } execution)
                    await execution.ReportSharedProjectionAsync(ApplyExecutionStatus(prepared.Apply(value), meanings, bundle.Facts));
            });
        return ApplyExecutionStatus(prepared.Apply(semantics), meanings, bundle.Facts);
    }

}

internal static class SharedSemanticText
{
    public static string TruncateSummary(this string value) => value.Length <= 500 ? value : value[..497] + "…";
}
