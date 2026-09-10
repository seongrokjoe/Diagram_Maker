using System.Text;
using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    // Reserve the largest configured output and template overhead before splitting.
    // This is an upper estimate, not a claim to know the serving model's tokenizer.
    private string CodeContextJson(object value)
    {
        var json = BoundedJson(value);
        var compact = new PromptIds().Encode(json);
        var output = Math.Max(_options.DiagramOutputTokens, Math.Max(_options.ReviewOutputTokens,
            _options.ThinkingOutputTokens ?? _options.OutputHardLimit));
        var limit = Math.Min(_options.MaxInputTokens, _options.MaxContextTokens - output - 4096);
        if (Encoding.UTF8.GetByteCount(compact) > limit)
            throw new DiagramGenerationException("EVIDENCE_INPUT_LIMIT", "필수 코드 문맥이 입력·출력 토큰 예산을 초과했습니다. 원문은 보존했습니다.");
        return json;
    }

    private async Task<IReadOnlyList<CodeBlockBehavior>> UnderstandCodeUnitAsync(CodeBlockWorkspaceInput input, CodeBlockGraph graph,
        CodeBlockGroupSelection group, CodeBlockSymbol original, IReadOnlyList<CodeBlockStep> steps, bool whole, CancellationToken ct)
    {
        var block = input.Blocks.First(b => b.Id == original.BlockId);
        var symbol = original with { Steps = steps, Calls = original.Calls.Where(c => whole || steps.Any(s =>
            s.Location.StartOffset <= c.Location.StartOffset && s.Location.EndOffset >= c.Location.EndOffset)).ToArray(),
            FlowEdges = original.FlowEdges.Where(e => steps.Any(s => s.Id == e.SourceId) && steps.Any(s => s.Id == e.TargetId)).ToArray() };
        var facts = steps.Select(s => s.Id).Append(symbol.Id).ToHashSet();
        var available = graph.Symbols.Where(s => s.Id != original.Id && graph.Relations.Any(r => r.Origin == "code" &&
            (r.FromSymbolId == original.Id && r.ToSymbolId == s.Id || r.ToSymbolId == original.Id && r.FromSymbolId == s.Id))).ToArray();
        try
        {
            var context = CodeContextJson(new { block.Title, block.Description, block.Language,
                code = whole ? block.Code[original.Location.StartOffset..original.Location.EndOffset] : null, symbol,
                partialContext = !whole, sourceSegments = whole ? null : steps.Where(s => s.Kind is not ("entry" or "exit"))
                    .Select(s => new { s.Id, s.Location, s.Statement, s.ControlPath, s.Definitions }),
                relatedSymbols = RelatedCodeSymbols(graph, [original.Id]),
                relations = graph.Relations.Where(r => r.FromSymbolId == symbol.Id || r.ToSymbolId == symbol.Id) });
            var policy = CodeBehaviorPolicy +
                "Describe the supplied symbol's role with factIds. nodeIds and edgeIds must be empty. recommendedType may be empty in this phase. " +
                "partialContext means only the supplied complete statements and their enclosing control paths are available. Do not infer missing statements or order across parts. " +
                "If a related symbol body is necessary, request up to 8 relatedSymbols ids in requestedSymbolIds, with empty behaviors. Otherwise leave requestedSymbolIds empty. Context can be requested once.";
            string? Validate(CodeBlockUnderstanding value) => value.Behaviors is not { Count: > 0 } || value.Behaviors.Any(b => b is null || string.IsNullOrWhiteSpace(b.Summary) ||
                    b.FactIds is not { Count: > 0 } || b.FactIds.Any(id => !facts.Contains(id)) || b.NodeIds is not { Count: 0 } || b.EdgeIds is not { Count: 0 })
                    ? "CodeUnderstandingEvidence" : null;
            var tokens = GetOutputTokens(Math.Min(_options.UnderstandingOutputTokens, _options.OutputHardLimit), input.EnableThinking);
            var result = await structured.CompleteAsync<CodeBlockUnderstanding>(policy, context, CodeUnderstandingSchema, tokens, input.EnableThinking,
                value => value.RequestedSymbolIds is { Count: > 0 } requested
                    ? requested.Count > 8 || requested.Any(id => available.All(s => s.Id != id)) ? "InvalidContextRequest" : null
                    : Validate(value), ct, allowRepair: true);
            if (result.Value.RequestedSymbolIds is { Count: > 0 } requestedIds)
            {
                var requested = available.Where(s => requestedIds.Contains(s.Id)).ToArray();
                facts.UnionWith(requested.SelectMany(s => s.Steps.Select(step => step.Id).Append(s.Id)));
                var expanded = CodeContextJson(new { context = JsonSerializer.Deserialize<JsonElement>(context), additionalContext = requested.Select(s => new {
                    s.Id, s.Signature, s.Location, s.Steps, s.Calls,
                    code = input.Blocks.First(b => b.Id == s.BlockId).Code[s.Location.StartOffset..s.Location.EndOffset] }) });
                result = await structured.CompleteAsync<CodeBlockUnderstanding>(policy + " This is the final supplied context. Return evidence-bound behaviors now; further requests are not permitted.",
                    expanded, CodeUnderstandingSchema, tokens, input.EnableThinking,
                    value => value.RequestedSymbolIds is { Count: > 0 } ? "ContextRequestLimit" : Validate(value), ct, allowRepair: false);
            }
            return result.Value.Behaviors;
        }
        catch (Exception e) when ((e is DiagramGenerationException { Code: "EVIDENCE_INPUT_LIMIT" } ||
            e is LlmClientException { Code: "LLM_INPUT_LIMIT" or "LLM_CONTEXT_LIMIT" or "LLM_RESPONSE_TRUNCATED" }) && steps.Count > 1)
        {
            var half = (steps.Count + 1) / 2;
            var first = await UnderstandCodeUnitAsync(input, graph, group, original, steps.Take(half).ToArray(), false, ct);
            var second = await UnderstandCodeUnitAsync(input, graph, group, original, steps.Skip(half).ToArray(), false, ct);
            return first.Concat(second).ToArray();
        }
    }

    private static object[] RelatedCodeSymbols(CodeBlockGraph graph, IReadOnlyList<string> symbolIds)
    {
        var related = graph.Relations.Where(r => r.Origin == "code" &&
            (r.FromSymbolId is not null && symbolIds.Contains(r.FromSymbolId) || r.ToSymbolId is not null && symbolIds.Contains(r.ToSymbolId)))
            .SelectMany(r => new[] { r.FromSymbolId, r.ToSymbolId }).Where(id => id is not null).ToHashSet();
        return graph.Symbols.Where(s => related.Contains(s.Id) && !symbolIds.Contains(s.Id))
            .Select(s => (object)new { s.Id, s.BlockId, s.Signature, s.Kind, s.Location, s.BaseTypes, s.Members }).ToArray();
    }
}
