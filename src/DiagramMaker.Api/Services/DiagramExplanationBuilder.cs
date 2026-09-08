using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramExplanationBuilder
{
    public static DiagramExplanation Fallback(DiagramIr diagram, EvidenceBundle bundle, string status, IReadOnlyList<string> warnings)
    {
        var ids = diagram.Nodes.SelectMany(node => node.SourceFactIds ?? []).Concat(diagram.Edges.SelectMany(edge => edge.SourceFactIds ?? [])).Distinct().ToArray();
        var evidence = diagram.Nodes.SelectMany(node => node.EvidenceIds).Concat(diagram.Edges.SelectMany(edge => edge.EvidenceIds))
            .Concat(bundle.Facts.Where(fact => ids.Contains(fact.Id)).SelectMany(fact => fact.EvidenceIds)).Distinct().ToArray();
        return new DiagramExplanation("코드에서 확인한 처리 구조입니다. 페이지의 목적과 변경 전후 의미 설명은 완료되지 않았습니다.",
            [], ids, evidence, status, warnings.Distinct().ToArray());
    }
}
