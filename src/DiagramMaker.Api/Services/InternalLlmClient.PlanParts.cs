using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    private async Task<SemanticGeneration?> PlanPartsAsync(DiagramIr candidate, EvidenceBundle bundle,
        ChangeUnderstanding? understanding, DiagramViewSelection selection, bool enableThinking,
        CancellationToken cancellationToken, int depth)
    {
        // Split only whole diagram elements/call sites. Their complete statements,
        // definitions, before/after source and control scopes travel with each part.
        // If even one such context cannot fit, reject meaning for the whole page.
        var count = candidate.Type == "sequence" ? candidate.Edges.Count : candidate.Nodes.Count;
        if (count < 2) return null;
        var pieces = new List<(DiagramIr Original, SemanticGeneration Generated)>();
        var size = (count + 1) / 2;
        for (var start = 0; start < count; start += size)
        {
            var edges = candidate.Type == "sequence" ? candidate.Edges.Skip(start).Take(size).ToArray() : [];
            var ids = candidate.Type == "sequence" ? edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).ToHashSet() :
                candidate.Nodes.Skip(start).Take(size).Select(node => node.Id).ToHashSet();
            var nodes = candidate.Nodes.Where(node => ids.Contains(node.Id)).ToArray();
            if (candidate.Type != "sequence") edges = candidate.Edges.Where(edge => ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)).ToArray();
            var part = candidate with { Nodes = nodes, Edges = edges, SequenceBlocks = candidate.SequenceBlocks is null ? null :
                SequenceStructure.Prune(candidate.SequenceBlocks, edges.Select(edge => edge.Id).ToHashSet(), ids) };
            var result = await PlanDiagramCoreAsync(part, bundle, understanding, selection, null, enableThinking, cancellationToken, depth + 1);
            if (result is null) return null;
            pieces.Add((part, result));
            if (result.Status != "Semantic") return new SemanticGeneration(candidate, "Deterministic",
                ["입력을 구문 단위로 분할했으나 필수 코드 문맥의 의미 검증을 완료하지 못했습니다.", .. result.Warnings], [],
                pieces.Sum(piece => piece.Generated.Attempts));
        }
        var mapping = new Dictionary<string, string>();
        foreach (var (original, generated) in pieces)
        foreach (var node in original.Nodes)
        {
            var match = generated.Diagram.Nodes.FirstOrDefault(value => value.Id == node.Id) ?? generated.Diagram.Nodes.FirstOrDefault(value =>
                node.SourceFactIds is { Count: > 0 } && node.SourceFactIds.All(id => (value.SourceFactIds ?? []).Contains(id)));
            if (match is null) return new SemanticGeneration(candidate, "Deterministic", ["분할된 처리 단계의 원본 연결을 확인하지 못했습니다."], [],
                pieces.Sum(piece => piece.Generated.Attempts));
            mapping[node.Id] = match.Id;
        }
        var elements = mapping.GroupBy(pair => pair.Value).Select(group => new SemanticElement(group.Key,
            pieces.SelectMany(piece => piece.Generated.Diagram.Nodes).First(node => node.Id == group.Key).Label,
            candidate.Nodes.Where(node => group.Any(pair => pair.Key == node.Id)).Select(node => node.Id).ToArray())).ToArray();
        var labels = pieces.SelectMany(piece => piece.Generated.Diagram.Edges).DistinctBy(edge => edge.Id).ToDictionary(edge => edge.Id, edge => edge.Label);
        var messages = candidate.Edges.Select(edge => new SemanticMessage(edge.Id, labels.GetValueOrDefault(edge.Id, edge.Label))).ToArray();
        var mergedPlan = new DiagramPlan("구문 단위 분할 결과", elements, messages, []);
        var failure = ValidatePlan(candidate, mergedPlan);
        if (failure is not null) return new SemanticGeneration(candidate, "Deterministic",
            ["분할 설계가 원본 제어 경계를 보존하지 못했습니다: " + failure], [], pieces.Sum(piece => piece.Generated.Attempts));
        var merged = ApplyPlan(candidate, mergedPlan);
        // Keep full details from independently reviewed parts; reconstruct only
        // relations across parts from the immutable original graph.
        merged = merged with { Nodes = pieces.SelectMany(piece => piece.Generated.Diagram.Nodes).DistinctBy(node => node.Id).ToArray() };
        validator.Validate(merged);
        _ = new MermaidCompiler(validator).Compile(merged);
        var explanations = pieces.Select(piece => piece.Generated.Explanation!).ToArray();
        return new SemanticGeneration(merged, "Semantic", [], pieces.SelectMany(piece => piece.Generated.InstructionResults).Distinct().ToArray(),
            pieces.Sum(piece => piece.Generated.Attempts), new DiagramExplanation(
                string.Join("\n", explanations.Select(explanation => explanation.Summary).Distinct()),
                explanations.SelectMany(explanation => explanation.Changes).ToArray(),
                explanations.SelectMany(explanation => explanation.FactIds).Distinct().ToArray(),
                explanations.SelectMany(explanation => explanation.EvidenceIds).Distinct().ToArray(), "Semantic",
                ["입력 한도에 맞춰 구문 단위로 나누어 검토했습니다. 각 부분의 설명을 함께 표시합니다."]));
    }
}
