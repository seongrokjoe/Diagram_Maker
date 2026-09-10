using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var prepared = new SharedSemanticProjection(true, facts);
        var projection = new CodeBlockProjectionService(new());
        foreach (var selection in selections)
            foreach (var page in projection.Build(graph, graph.Relations, group, selection))
                prepared.Add(new(selection.Id + "/" + page.Id, page.Diagram, selection));
        object Sources(IReadOnlyList<SharedSemanticItem> items)
        {
            var ids = items.SelectMany(i => i.FactIds).ToHashSet();
            var spans = facts.Where(f => ids.Contains(f.Id) && f.Span is not null).Select(f => f.Span!).ToArray();
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
                excerpts, facts = facts.Where(f => ids.Contains(f.Id)).Select(f => new
                { f.Id, f.Kind, blockId = f.Span?.FilePath, start = f.Span?.StartOffset, end = f.Span?.EndOffset,
                    description = f.Span is null ? f.Label : null }) };
        }
        var semantics = await GenerateSharedAsync("code-block", group.Title, prepared.Items.Values.ToArray(), Sources,
            selections, input.EnableThinking, cancellationToken);
        return prepared.Apply(semantics);
    }

    public async Task<SharedDiagramGroup?> PlanGitGroupAsync(EvidenceBundle bundle, IReadOnlyList<SharedDiagramInput> diagrams,
        bool enableThinking, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return null;
        var prepared = new SharedSemanticProjection(false, bundle.Facts);
        foreach (var diagram in diagrams) prepared.Add(diagram);
        var represented = diagrams.SelectMany(d => d.Diagram.Nodes.SelectMany(n => n.SourceFactIds ?? [])
            .Concat(d.Diagram.Edges.SelectMany(e => e.SourceFactIds ?? []))).ToHashSet();
        prepared.AddChanges(bundle.Facts.Where(f => represented.Contains(f.Id)).SelectMany(f => f.ChangeIds));
        object Sources(IReadOnlyList<SharedSemanticItem> items)
        {
            var ids = items.SelectMany(i => i.FactIds).ToHashSet();
            var changes = items.SelectMany(i => i.ChangeIds).ToHashSet();
            // Old and new code for a selected change always travel together.
            var facts = bundle.Facts.Where(f => ids.Contains(f.Id) || f.Kind == "source" && f.ChangeIds.Any(changes.Contains)).ToArray();
            // Evidence IDs, blob IDs and full CodeContext objects stay in the
            // server-owned graph. Repeating those on every source line makes
            // overlapping type/method changes dominate the prompt.
            return new { bundle.BaseSha, bundle.TargetSha, facts = facts.Select(f => new
                { f.Id, f.Kind, f.ChangeIds, label = f.Kind == "source" ? null : f.Label,
                    revision = f.Span?.RevisionSha, file = f.Span?.FilePath,
                    startLine = f.Span?.StartLine, endLine = f.Span?.EndLine, f.Content }) };
        }
        var semantics = await GenerateSharedAsync("git", "변경 전후 코드의 동작", prepared.Items.Values.ToArray(), Sources,
            diagrams.Select(d => d.Selection).Distinct().ToArray(), enableThinking, cancellationToken);
        return prepared.Apply(semantics);
    }

    private async Task<SharedSemanticResponse> GenerateSharedAsync(string sourceKind, string title,
        IReadOnlyList<SharedSemanticItem> items, Func<IReadOnlyList<SharedSemanticItem>, object> sources,
        IReadOnlyList<DiagramViewSelection> selections, bool thinking, CancellationToken ct)
    {
        var available = selections.Select(s => s.DiagramType).Distinct().ToArray();
        var outputTokens = Math.Min(thinking ? GetThinkingOutputTokens() : Math.Min(8000, _options.DiagramOutputTokens), _options.OutputHardLimit);
        var reviewTokens = Math.Min(thinking ? GetThinkingOutputTokens() : Math.Min(2000, _options.ReviewOutputTokens), _options.OutputHardLimit);
        var inputLimit = Math.Min(48000, Math.Min(_options.MaxInputTokens, _options.MaxContextTokens - outputTokens - 4096));
        var instructions = selections.Select(s => s.RefinementInstruction).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        object Context(IReadOnlyList<SharedSemanticItem> batch) => new
        {
            sourceKind, title, available, instructions, sources = sources(batch),
            items = batch.Select(i => new { i.Id, i.Kind, i.Label, i.FactIds, i.ChangeIds, i.RefinementInstruction,
                scopes = i.Contexts.Select(c => new { c.Purpose, c.ControlPath })
                    .DistinctBy(c => JsonSerializer.Serialize(c, PromptJson.Options)),
                calls = i.Contexts.Where(c => c.Purpose is "call" or "assertion").Select(c => new
                    { c.Target, c.Receiver, c.Arguments, c.AssignedTo, c.CreatedType, c.Initializers }).Distinct(),
                details = i.Kind is "type" or "control" ? i.Details : [] })
        };
        string Serialize(IReadOnlyList<SharedSemanticItem> batch) => BoundedJson(Context(batch));
        bool Fits(IReadOnlyList<SharedSemanticItem> batch)
        {
            try
            {
                // Bounded prompt bytes provide a conservative offline fallback.
                // Limit output cardinality as well as input; output is compact and
                // never repeats node/fact lists already owned by the server.
                return batch.Count <= Math.Max(1, Math.Min(outputTokens, 8000) / 100) &&
                    Encoding.UTF8.GetByteCount(new PromptIds().Encode(Serialize(batch))) + Math.Min(outputTokens, Math.Min(8000, inputLimit / 6)) +
                    Math.Min(4096, inputLimit / 4) <= inputLimit;
            }
            catch (DiagramGenerationException) { return false; }
        }
        var batches = new List<IReadOnlyList<SharedSemanticItem>>();
        var current = new List<SharedSemanticItem>();
        foreach (var item in items.OrderBy(i => i.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(i => i.Location?.StartLine ?? 0).ThenBy(i => i.Location?.StartOffset ?? 0)
            .ThenBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Label, StringComparer.Ordinal))
        {
            if (current.Count > 0 && !Fits(current.Append(item).ToArray())) { batches.Add(current.ToArray()); current.Clear(); }
            current.Add(item);
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        var annotations = new List<SharedSemanticAnnotation>();
        var summaries = new List<string>();
        var recommendation = available.FirstOrDefault() ?? "code-relation";
        var consecutiveFailures = 0;
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            if (consecutiveFailures >= 3) break;
            SharedSemanticResponse? result;
            try { result = await GenerateBatch(batch, 0); }
            catch (LlmClientException) { break; } // Preserve completed batches; unfinished transport work remains resumable.
            if (result is null || result.Items.Count == 0) continue;
            annotations.AddRange(result.Items); summaries.Add(result.Summary);
            recommendation = result.RecommendedType;
        }
        // Group prose uses only already-reviewed meanings; no page-sized code
        // prompt is constructed a second time merely to name an overview.
        return new(string.Join("\n", summaries.Distinct()).TruncateSummary(), recommendation, annotations);

        async Task<SharedSemanticResponse?> GenerateBatch(IReadOnlyList<SharedSemanticItem> batch, int depth)
        {
            if (consecutiveFailures >= 3) return null;
            if (!Fits(batch))
            {
                if (batch.Count < 2 || depth >= 12) { consecutiveFailures++; return null; }
                return await Split();
            }
            var context = Serialize(batch);
            var ids = batch.Select(i => i.Id).ToHashSet();
            string? Validate(SharedSemanticResponse value) => string.IsNullOrWhiteSpace(value.Summary) || value.Summary.Length > 500 ||
                !available.Contains(value.RecommendedType) || value.Items is null || value.Items.Count != ids.Count ||
                !value.Items.Select(i => i?.Id).ToHashSet().SetEquals(ids) || value.Items.Any(i => i is null ||
                    !Natural(i.Summary, 80) || !Natural(i.Description, 500)) ? "SharedSemanticCoverage" : null;
            var result = await SemanticExecution.RunAsync("shared-" + sourceKind, context, async () =>
            {
                object? rejected = null;
                string[] issues = [];
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var prompt = attempt == 0 ? context : BoundedJson(new { context = JsonSerializer.Deserialize<JsonElement>(context), rejected, issues });
                        var planned = await structured.CompleteAsync<SharedSemanticResponse>(EvidencePolicy +
                            "Create one Korean semantic annotation for EVERY supplied item ID. Do not return diagrams, node lists, fact lists or source copies. " +
                            "An item can occur in many diagram formats and pages: interpret its source once. Summary is a short meaningful label; description explains arguments, outcomes and evidence limits. " +
                            "Decisions and controls must retain the exact predicate polarity. Preparation chains preserve all their statements in source evidence. " +
                            "For git change items compare BOTH revisions and distinguish added, removed and retained behavior; do not call a pointer assignment allocation. " +
                            "For source symbols describe their own role without inventing the body of missing callees. Honor each item's own refinementInstruction without inventing facts or applying another item's instruction. " +
                            "Recommend one available type. Every label and description must be concise Korean prose, without code operators, markdown or HTML.",
                            prompt, SharedSchema, outputTokens, thinking, Validate, ct,
                            _options.NaturalDiagramTemperature, _options.NaturalDiagramSeed, allowRepair: false, inputTokenLimit: inputLimit);
                        rejected = planned.Value;
                        var reviewed = await structured.CompleteAsync<DiagramPlanReview>(EvidencePolicy +
                            "Independently check each annotation against its supplied source and facts. Reject invented roles, missing core actions or assertions, reversed branch polarity, " +
                            "unsupported business meaning, mixed function scopes, and retained behavior described as a new Git change. " +
                            "Every annotated preparation chain must express all its observed outcomes. Return accepted and issues, without source copies.",
                            BoundedJson(new { context = JsonSerializer.Deserialize<JsonElement>(context), proposed = planned.Value }), PlanReviewSchema,
                            reviewTokens, thinking, value => value.Issues is null || value.Accepted == (value.Issues.Count > 0) ? "InvalidReview" : null,
                            ct, allowRepair: false, inputTokenLimit: inputLimit);
                        if (reviewed.Value.Accepted) return planned.Value;
                        issues = reviewed.Value.Issues.ToArray();
                    }
                    catch (LlmClientException error) when (error.Code is "LLM_INPUT_LIMIT" or "LLM_CONTEXT_LIMIT" or "LLM_RESPONSE_TRUNCATED")
                    {
                        if (batch.Count > 1 && depth < 12) return await Split();
                        return new SharedSemanticResponse("입력 또는 출력 한도로 의미 검토 미완료", recommendation, []);
                    }
                    catch (LlmClientException error) when (error.Code == "LLM_SCHEMA_INVALID")
                    { rejected = error.RejectedContent; issues = [error.FailureKind ?? error.Code]; }
                }
                return new SharedSemanticResponse("의미 검토 미완료", recommendation, []);
            }, value => value.Items.Count > 0);
            consecutiveFailures = result?.Items.Count > 0 ? 0 : consecutiveFailures + 1;
            return result;

            async Task<SharedSemanticResponse?> Split()
            {
                if (SemanticExecution.Current is { } execution) await execution.MarkSplitAsync();
                var half = (batch.Count + 1) / 2;
                var first = await GenerateBatch(batch.Take(half).ToArray(), depth + 1);
                var second = await GenerateBatch(batch.Skip(half).ToArray(), depth + 1);
                if (first is null) return second;
                if (second is null) return first;
                return new((first.Summary + "\n" + second.Summary).TruncateSummary(), first.RecommendedType,
                    first.Items.Concat(second.Items).ToArray());
            }
        }
    }

    private static bool Natural(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        Regex.IsMatch(value, "[가-힣]") && !Regex.IsMatch(value, @"[<>`;{}]|&&|\|\||==|!=");
}

internal static class SharedSemanticText
{
    public static string TruncateSummary(this string value) => value.Length <= 500 ? value : value[..497] + "…";
}
