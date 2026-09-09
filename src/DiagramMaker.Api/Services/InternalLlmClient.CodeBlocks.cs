using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    public const string CodeBlockPromptVersion = "code-block-semantic-v2";
    private const string CodeBehaviorPolicy = EvidencePolicy +
        "This is pasted code behavior, with no Git history or changes. Explain preparation, core work, validation and error handling using actual arguments and assignments. " +
        "User relations are user assertions, not code proof or execution order. Missing context stays unknown. Preserve conditions, loops, early returns and failures. " +
        "The reader does not know programming syntax. Use specific, easy Korean descriptions of actions, decisions and outcomes. " +
        "Keep original expressions, exceptions, numeric arguments and source locations in evidence and behavior explanations. " +
        "GetRequiredService resolves a registered service; do not claim it always creates a new service. Never infer the body of an unavailable function. ";
    private static readonly JsonElement CodeUnderstandingSchema = ParseSchema("""
        {"type":"object","additionalProperties":false,"properties":{
          "summary":{"type":"string","maxLength":500},"recommendedType":{"type":"string"},
          "behaviors":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "id":{"type":"string"},"summary":{"type":"string","maxLength":500},"factIds":{"type":"array","items":{"type":"string"}},
            "nodeIds":{"type":"array","items":{"type":"string"}},"edgeIds":{"type":"array","items":{"type":"string"}}},
            "required":["id","summary","factIds","nodeIds","edgeIds"]}}},"required":["summary","recommendedType","behaviors"]}
        """);
    private static readonly JsonElement CodePlanSchema = ParseSchema("""
        {"type":"object","additionalProperties":false,"properties":{
          "summary":{"type":"string","maxLength":500},
          "elements":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "id":{"type":"string"},"summary":{"type":"string","maxLength":80},"nodeIds":{"type":"array","items":{"type":"string"}},
            "factIds":{"type":"array","items":{"type":"string"}},"condition":{"type":"string","maxLength":240},"outcome":{"type":"string","maxLength":240}},
            "required":["id","summary","nodeIds","factIds","condition","outcome"]}},
          "messages":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "edgeId":{"type":"string"},"summary":{"type":"string","maxLength":120}},"required":["edgeId","summary"]}},
          "behaviors":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "id":{"type":"string"},"summary":{"type":"string","maxLength":500},"factIds":{"type":"array","items":{"type":"string"}},
            "nodeIds":{"type":"array","items":{"type":"string"}},"edgeIds":{"type":"array","items":{"type":"string"}}},
            "required":["id","summary","factIds","nodeIds","edgeIds"]}},
          "controls":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "id":{"type":"string"},"label":{"type":"string","maxLength":120}},"required":["id","label"]}}},
          "required":["summary","elements","messages","behaviors","controls"]}
        """);

    public async Task<CodeBlockUnderstanding?> UnderstandCodeBlocksAsync(CodeBlockWorkspaceInput input, CodeBlockGraph graph,
        CodeBlockGroupSelection group, IReadOnlyList<DiagramAvailability> availability, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return null;
        var behaviors = new List<CodeBlockBehavior>();
        // Each function/type is a complete context unit. Never truncate a source body.
        foreach (var symbol in graph.Symbols.Where(s => group.BlockIds.Contains(s.BlockId) && (s.Steps.Count > 0 || s.OwnerId is null)))
        {
            var block = input.Blocks.First(b => b.Id == symbol.BlockId);
            var facts = symbol.Steps.Select(s => s.Id).Append(symbol.Id).ToHashSet();
            var result = await structured.CompleteAsync<CodeBlockUnderstanding>(CodeBehaviorPolicy +
                "Describe the supplied symbol's role with factIds. nodeIds and edgeIds must be empty. recommendedType may be empty in this phase.",
                BoundedJson(new { block.Title, block.Description, block.Language, code = block.Code[symbol.Location.StartOffset..symbol.Location.EndOffset], symbol,
                    relatedBlocks = input.Blocks.Where(b => group.BlockIds.Contains(b.Id) && b.Id != block.Id).Select(b => new { b.Id, b.Title, b.Description }),
                    relations = graph.Relations.Where(r => r.FromSymbolId == symbol.Id || r.ToSymbolId == symbol.Id) }),
                CodeUnderstandingSchema, GetOutputTokens(_options.DiagramOutputTokens, input.EnableThinking), input.EnableThinking,
                value => value.Behaviors is not { Count: > 0 } || value.Behaviors.Any(b => string.IsNullOrWhiteSpace(b.Summary) ||
                    b.FactIds is not { Count: > 0 } || b.FactIds.Any(id => !facts.Contains(id)) || b.NodeIds is not { Count: 0 } || b.EdgeIds is not { Count: 0 })
                    ? "CodeUnderstandingEvidence" : null, cancellationToken, allowRepair: false);
            behaviors.AddRange(result.Value.Behaviors);
        }
        var recommendation = await structured.CompleteAsync<CodeBlockUnderstanding>(CodeBehaviorPolicy +
            "Summarize this group and recommend exactly one AVAILABLE diagram type. Same group does not imply a relationship. Return behaviors as an empty array.",
            BoundedJson(new { group.Title, behaviors, availability, relations = graph.Relations.Where(r => group.BlockIds.Contains(r.FromBlockId) && group.BlockIds.Contains(r.ToBlockId)) }),
            CodeUnderstandingSchema, GetOutputTokens(_options.ReviewOutputTokens, input.EnableThinking), input.EnableThinking,
            value => string.IsNullOrWhiteSpace(value.Summary) || !availability.Any(a => a.Available && a.Type == value.RecommendedType)
                ? "UnavailableRecommendation" : null, cancellationToken, allowRepair: false);
        return recommendation.Value with { Behaviors = behaviors };
    }

    public async Task<SemanticGeneration?> PlanCodeBlockDiagramAsync(DiagramIr candidate, CodeBlockWorkspaceInput input, CodeBlockGraph graph,
        CodeBlockUnderstanding? understanding, DiagramViewSelection selection, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return null;
        var evidenceIds = candidate.Nodes.SelectMany(n => n.EvidenceIds).Concat(candidate.Edges.SelectMany(e => e.EvidenceIds)).ToHashSet();
        var excerpts = graph.Evidence.Where(e => evidenceIds.Contains(e.Id)).ToArray();
        var localFacts = candidate.Nodes.SelectMany(n => n.SourceFactIds ?? []).Concat(candidate.Edges.SelectMany(e => e.SourceFactIds ?? [])).ToHashSet();
        var symbols = graph.Symbols.Where(s => localFacts.Contains(s.Id) || s.Steps.Any(step => localFacts.Contains(step.Id)) ||
            s.Calls.Any(call => localFacts.Contains(call.Id)) || graph.Transitions.Any(t => t.SymbolId == s.Id && localFacts.Contains(t.Id))).ToArray();
        var functions = symbols.Select(s => { var b = input.Blocks.First(b => b.Id == s.BlockId); return new
            { s.Id, s.BlockId, s.Name, s.Kind, s.Signature, s.Location, s.Calls, b.Title, b.Description, b.Language,
                code = b.Code[s.Location.StartOffset..s.Location.EndOffset] }; }).ToArray();
        var context = new { candidate, excerpts, functions, relations = graph.Relations.Where(r =>
            symbols.Any(s => s.BlockId == r.FromBlockId || s.BlockId == r.ToBlockId)), understanding = understanding is null ? null : understanding with
            { Behaviors = understanding.Behaviors.Where(b => b.FactIds.Any(localFacts.Contains)).ToArray() }, selection };
        try { BoundedJson(context); }
        catch (DiagramGenerationException)
        {
            var units = candidate.Nodes.GroupBy(n => n.Group ?? n.Id).Select(g => g.ToArray()).ToArray();
            if (units.Length <= 1 || candidate.Type == "sequence")
                return new SemanticGeneration(candidate, "Incomplete", ["의미 설명 미완료: 한 함수·제어 문맥이 LLM 입력 한도를 초과합니다. 원문을 자르지 않았습니다."], [], 0, FailureStage: "input-limit");
            var pieces = new List<SemanticGeneration>();
            foreach (var unit in units)
            {
                var ids = unit.Select(n => n.Id).ToHashSet();
                var part = candidate with { Nodes = unit, Edges = candidate.Edges.Where(e => ids.Contains(e.SourceId) && ids.Contains(e.TargetId)).ToArray() };
                pieces.Add((await PlanCodeBlockDiagramAsync(part, input, graph, understanding, selection, cancellationToken))!);
            }
            if (pieces.Any(p => p.Status != "Semantic")) return new SemanticGeneration(candidate, "Incomplete",
                pieces.SelectMany(p => p.Warnings).Distinct().ToArray(), [], pieces.Sum(p => p.Attempts), FailureStage: pieces.First(p => p.Status != "Semantic").FailureStage);
            var nodes = pieces.SelectMany(p => p.Diagram.Nodes).ToArray();
            var mapping = candidate.Nodes.ToDictionary(n => n.Id, n => nodes.First(p => p.Id == n.Id ||
                p.Group == n.Group && (n.SourceFactIds ?? [n.Id]).All(id => (p.SourceFactIds ?? []).Contains(id))).Id);
            var edges = candidate.Edges.Where(e => mapping[e.SourceId] != mapping[e.TargetId] || e.SourceId == e.TargetId)
                .Select(e => e with { SourceId = mapping[e.SourceId], TargetId = mapping[e.TargetId] }).ToArray();
            var complete = pieces.All(p => p.Status == "Semantic");
            var warnings = pieces.SelectMany(p => p.Warnings).Distinct().ToArray();
            var behaviors = pieces.SelectMany(p => p.Explanation?.Behaviors ?? []).ToArray();
            return new SemanticGeneration(candidate with { Nodes = nodes, Edges = edges }, complete ? "Semantic" : "Incomplete", warnings, [], pieces.Sum(p => p.Attempts),
                new DiagramExplanation(understanding?.Summary ?? "함수별 코드 동작과 확인된 관계", [], behaviors.SelectMany(b => b.FactIds).Distinct().ToArray(),
                    evidenceIds.ToArray(), complete ? "Semantic" : "Incomplete", warnings, Behaviors: behaviors));
        }
        var issues = Array.Empty<string>();
        var failureStage = "plan-validation";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await structured.CompleteAsync<CodeBlockSemanticPlan>(CodeBehaviorPolicy +
                    "Plan Korean semantic labels and behavior explanations. Cover every original node once in elements. Cite sourceFactIds in behaviors. " +
                    "Merge consecutive actions within exactly the same function and branch when they form one meaningful outcome. " +
                    "For example setting status 403, writing an error response and returning can be one step: '403 오류 응답을 보내고 종료'. " +
                    "Never merge decisions, loops, entry/exit, branches, function boundaries or calls with a proven cross-function edge. A return may only end a merged step. " +
                    "Every element must cite ALL its original sourceFactIds. condition is a natural Korean decision (empty for actions); outcome describes the result. " +
                    "Do not create, delete or reorder calls/relationships. Supply messages for sequence/state edges and controls for every sequence alt/loop block. " +
                    "Preserve the original predicate polarity: true still means the original condition is true. Do not reverse a question's meaning. " +
                    "Each behavior must reference actual nodes/edges and retain original conditions, exceptions and relevant argument values. " +
                    "Use selection.overrides.detailLevel and presetId: compact groups more eligible chains; detailed explains individual actions. Never omit control paths for brevity.",
                    BoundedJson(new { context, repairIssues = issues }), CodePlanSchema,
                    GetOutputTokens(_options.DiagramOutputTokens, input.EnableThinking), input.EnableThinking,
                    value => ValidateCodePlan(candidate, value), cancellationToken, allowRepair: false);
                var review = await structured.CompleteAsync<DiagramPlanReview>(CodeBehaviorPolicy +
                    "Review the plan against the supplied source. Reject unsupported behavior, lost core actions/arguments/assertions, incorrect branches or invented execution order. " +
                    "Check that source facts support every semantic explanation. Return accepted and issues.",
                    BoundedJson(new { context, proposedPlan = result.Value }), PlanReviewSchema,
                    GetOutputTokens(_options.ReviewOutputTokens, input.EnableThinking), input.EnableThinking,
                    value => value.Issues is null ? "MissingReview" : null, cancellationToken, allowRepair: false);
                if (!review.Value.Accepted) { failureStage = "semantic-review"; issues = review.Value.Issues.ToArray(); continue; }
                var plan = new DiagramPlan(result.Value.Summary, result.Value.Elements.Select(e => new SemanticElement(e.Id, e.Summary, e.NodeIds)).ToArray(), result.Value.Messages, []);
                var projected = ApplyCodePlan(candidate, result.Value);
                var mapping = plan.Elements.SelectMany(e => e.NodeIds.Select(id => (id, mapped: e.NodeIds.Count == 1 ? id : StableIds.Create("semantic-block", string.Join("|", e.NodeIds)))))
                    .ToDictionary(e => e.id, e => e.mapped);
                var retainedEdges = projected.Edges.Select(e => e.Id).ToHashSet();
                var behaviors = result.Value.Behaviors.Select(b => b with { NodeIds = b.NodeIds.Select(id => mapping[id]).Distinct().ToArray(),
                    EdgeIds = b.EdgeIds.Where(retainedEdges.Contains).ToArray() }).ToArray();
                var explanation = new DiagramExplanation(plan.Summary, [], behaviors.SelectMany(b => b.FactIds).Distinct().ToArray(),
                    evidenceIds.ToArray(), "Semantic", [], Behaviors: behaviors);
                validator.Validate(projected);
                return new SemanticGeneration(projected, "Semantic", [], [], attempt + 1, explanation);
            }
            catch (Exception e) when (e is LlmClientException or DiagramGenerationException or DiagramValidationException)
            {
                failureStage = e is LlmClientException llmError && llmError.Code != "LLM_SCHEMA_INVALID" ? "llm-request" : "plan-validation";
                issues = [e is LlmClientException detail ? detail.FailureKind ?? detail.Code : "InvalidCodePlan"];
            }
        }
        return new SemanticGeneration(candidate, "Incomplete", [failureStage switch
        {
            "semantic-review" => "의미 검토 실패: 원본 동작과 설명의 일치를 확인하지 못했습니다.",
            "llm-request" => "LLM 요청 실패: 응답 또는 시간 제한을 확인하세요.",
            _ => "의미 계획 검증 실패: 구조·근거·분기 또는 코드 범위를 확인하지 못했습니다."
        }], [], 2, FailureStage: failureStage);
    }

    internal static string? ValidateCodePlan(DiagramIr candidate, CodeBlockSemanticPlan plan)
    {
        var invalid = CodeBlockPlanValidation.Validate(candidate, plan);
        if (invalid is not null) return invalid;
        if (plan.Behaviors is not { Count: > 0 }) return "MissingBehaviors";
        var nodes = candidate.Nodes.ToDictionary(n => n.Id);
        var edges = candidate.Edges.ToDictionary(e => e.Id);
        var factIds = nodes.Values.SelectMany(n => n.SourceFactIds ?? []).Concat(edges.Values.SelectMany(e => e.SourceFactIds ?? [])).ToHashSet();
        foreach (var b in plan.Behaviors)
        {
            if (b is null || string.IsNullOrWhiteSpace(b.Summary) || b.Summary.Length > 500 || b.FactIds is not { Count: > 0 } ||
                b.FactIds.Any(id => !factIds.Contains(id)) || b.NodeIds is null || b.EdgeIds is null || b.NodeIds.Count + b.EdgeIds.Count == 0 ||
                b.NodeIds.Any(id => !nodes.ContainsKey(id)) || b.EdgeIds.Any(id => !edges.ContainsKey(id))) return "InvalidBehaviorEvidence";
            var actual = b.NodeIds.SelectMany(id => nodes[id].SourceFactIds ?? []).Concat(b.EdgeIds.SelectMany(id => edges[id].SourceFactIds ?? [])).ToHashSet();
            if (b.FactIds.Any(id => !actual.Contains(id))) return "UnrelatedBehaviorEvidence";
        }
        if (nodes.Keys.Any(id => plan.Behaviors.All(b => !b.NodeIds.Contains(id)))) return "MissingBehaviorCoverage";
        return null;
    }
}
