using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramEvidenceBuilder
{
    public static EvidenceBundle Build(VersionedGraph graph, GitComparison comparison, IReadOnlyList<string> changeIds)
    {
        var changes = graph.Changes.Where(change => changeIds.Contains(change.Id, StringComparer.Ordinal)).ToArray();
        var selectedVersions = changes.SelectMany(change => new[] { change.BeforeSymbolVersionId, change.AfterSymbolVersionId })
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var selectedIdentities = graph.Versions.Where(version => selectedVersions.Contains(version.Id))
            .Select(version => version.IdentityId).ToHashSet(StringComparer.Ordinal);
        var related = selectedIdentities.ToHashSet(StringComparer.Ordinal);
        related.UnionWith(graph.Versions.Where(version => related.Contains(version.IdentityId))
            .Select(version => version.OwnerIdentityId).OfType<string>().ToArray());
        for (var depth = 0; depth < 3; depth++)
        {
            var next = graph.Edges.Where(edge => related.Contains(edge.FromIdentityId) || related.Contains(edge.ToIdentityId))
                .SelectMany(edge => new[] { edge.FromIdentityId, edge.ToIdentityId }).ToArray();
            related.UnionWith(next);
            related.UnionWith(graph.Versions.Where(version => related.Contains(version.IdentityId))
                .Select(version => version.OwnerIdentityId).OfType<string>().ToArray());
        }
        var facts = new List<SourceFact>();
        var warnings = new List<string>();
        if (graph.Versions.Where(version => related.Contains(version.IdentityId)).GroupBy(version => (version.IdentityId, version.RevisionSha))
            .Any(group => group.Select(version => version.FilePath).Distinct().Count() > 1))
            warnings.Add("여러 파일에 걸친 선언이 있습니다. 수집된 멤버는 합쳐 표시하지만 부분 선언별 변경 범위는 소스 근거에서 확인해야 합니다.");
        foreach (var version in graph.Versions.Where(version => related.Contains(version.IdentityId)))
        {
            var ids = changes.Where(change => change.BeforeSymbolVersionId == version.Id || change.AfterSymbolVersionId == version.Id)
                .Select(change => change.Id).ToArray();
            var evidence = graph.Evidence.Where(item => item.RevisionSha == version.RevisionSha && item.FilePath == version.FilePath &&
                item.StartLine >= version.StartLine && item.EndLine <= version.EndLine).ToArray();
            var primary = evidence.FirstOrDefault();
            var file = comparison.Files.FirstOrDefault(item =>
                (version.RevisionSha == comparison.BaseSha ? item.PreviousPath ?? item.Path : item.Path) == version.FilePath);
            var content = file is not null ? version.RevisionSha == comparison.BaseSha ? file.BeforeContent : file.AfterContent :
                comparison.ContextFiles?.FirstOrDefault(item => item.RevisionSha == version.RevisionSha && item.Path == version.FilePath)?.Content;
            var span = new SourceSpan(version.RevisionSha, primary?.BlobOid ?? "", version.FilePath, version.StartLine, version.EndLine,
                primary?.StartOffset, primary?.EndOffset);
            facts.Add(new SourceFact(version.Id, "symbol", version.Signature, ids, evidence.Select(item => item.Id).ToArray(), span));
            if (content is null)
            {
                if (ids.Length > 0) warnings.Add($"소스 본문 미확보: {version.FilePath}:{version.StartLine} ({version.RevisionSha[..Math.Min(8, version.RevisionSha.Length)]})");
                continue;
            }
            // Source chunks are whole lines, each with an exact revision/range. A long
            // line is retained intact; the request packer reports oversized facts.
            var lines = content.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            var end = Math.Min(lines.Length, version.EndLine);
            for (var start = Math.Max(0, version.StartLine - 1); start < end;)
            {
                var stop = start;
                var size = 0;
                while (stop < end && (stop == start || size + lines[stop].Length < 4000)) size += lines[stop++].Length + 1;
                facts.Add(new SourceFact(StableIds.Create(version.Id, "source", start, stop), "source", version.QualifiedName,
                    ids, evidence.Select(item => item.Id).ToArray(), span with { StartLine = start + 1, EndLine = stop,
                        StartOffset = null, EndOffset = null }, string.Join('\n', lines[start..stop])));
                start = stop;
            }
        }
        foreach (var flow in graph.ControlFlows ?? [])
        {
            if (!related.Contains(flow.IdentityId)) continue;
            foreach (var node in flow.Nodes)
                facts.Add(new SourceFact(node.Id, node.Kind, node.Label, ChangesFor(flow.IdentityId), node.EvidenceIds,
                    new SourceSpan(flow.RevisionSha ?? comparison.TargetSha, BlobFor(flow.RevisionSha, flow.FilePath), flow.FilePath ?? "", node.StartLine, node.EndLine),
                    Context: node.Context));
        }
        foreach (var edge in graph.Edges.Where(edge => related.Contains(edge.FromIdentityId) && related.Contains(edge.ToIdentityId)))
            facts.Add(new SourceFact(edge.Id, edge.Type, $"{edge.FromIdentityId} → {edge.ToIdentityId}: {edge.Label}",
                ChangesFor(edge.FromIdentityId).Concat(ChangesFor(edge.ToIdentityId)).Distinct().ToArray(), edge.EvidenceIds,
                new SourceSpan(edge.RevisionSha ?? comparison.TargetSha, BlobFor(edge.RevisionSha, edge.FilePath), edge.FilePath ?? "", edge.StartLine ?? 1, edge.EndLine ?? edge.StartLine ?? 1,
                    graph.Evidence.FirstOrDefault(item => edge.EvidenceIds.Contains(item.Id))?.StartOffset,
                    graph.Evidence.FirstOrDefault(item => edge.EvidenceIds.Contains(item.Id))?.EndOffset), Context: edge.Context));
        if (comparison.ContextFilesTruncated) warnings.Add("저장소 문맥 수집 한도로 일부 관계가 분석되지 않았습니다.");
        var ordered = facts.Where(fact => fact.Kind != "source").Concat(DeduplicateSources(facts))
            .DistinctBy(fact => fact.Id).OrderByDescending(fact => fact.ChangeIds.Count > 0).ThenBy(fact => fact.Id).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { comparison.BaseSha, comparison.TargetSha, changeIds = changeIds.Order().ToArray(), facts = ordered, SourceGraphAnalyzer.IndexVersion })))).ToLowerInvariant();
        return new EvidenceBundle(hash, comparison.BaseSha, comparison.TargetSha, changeIds, ordered, warnings.Distinct().ToArray());

        string[] ChangesFor(string identityId) => changes.Where(change => graph.Versions.Any(version => version.IdentityId == identityId &&
            (version.Id == change.BeforeSymbolVersionId || version.Id == change.AfterSymbolVersionId))).Select(change => change.Id).ToArray();
        string BlobFor(string? revision, string? path) => graph.Evidence.FirstOrDefault(item =>
            item.RevisionSha == (revision ?? comparison.TargetSha) && item.FilePath == path)?.BlobOid ?? "";
    }

    private static IEnumerable<SourceFact> DeduplicateSources(IReadOnlyList<SourceFact> facts)
    {
        foreach (var file in facts.Where(fact => fact.Kind == "source" && fact.Span is not null && fact.Content is not null)
            .GroupBy(fact => (fact.Span!.RevisionSha, fact.Span.FilePath)))
        {
            // Source bodies of types and their methods overlap. Store each line once,
            // with all applicable change/evidence IDs, while retaining exact ranges.
            var values = file.OrderBy(fact => fact.Span!.StartLine).ThenByDescending(fact => fact.Span!.EndLine).ToArray();
            var boundaries = values.SelectMany(fact => new[] { fact.Span!.StartLine, fact.Span.EndLine + 1 }).Distinct().Order().ToArray();
            for (var index = 1; index < boundaries.Length; index++)
            {
                var start = boundaries[index - 1];
                var end = boundaries[index] - 1;
                var sources = values.Where(fact => fact.Span!.StartLine <= start && fact.Span.EndLine >= end).ToArray();
                if (sources.Length == 0) continue;
                var source = sources[0];
                yield return source with { Id = StableIds.Create("source", file.Key.RevisionSha, file.Key.FilePath, start, end),
                    ChangeIds = sources.SelectMany(fact => fact.ChangeIds).Distinct().Order().ToArray(),
                    EvidenceIds = sources.SelectMany(fact => fact.EvidenceIds).Distinct().Order().ToArray(),
                    Span = source.Span! with { StartLine = start, EndLine = end, StartOffset = null, EndOffset = null },
                    Content = string.Join('\n', source.Content!.Split('\n').Skip(start - source.Span!.StartLine).Take(end - start + 1)) };
            }
        }
    }

    public static DiagramStyleOverrides ResolveOptions(DiagramPreset preset, DiagramViewSelection selection) => new(
        selection.Overrides?.Direction ?? preset.Direction, selection.Overrides?.DetailLevel ?? preset.DetailLevel,
        selection.Overrides?.CallerDepth ?? preset.CallerDepth, selection.Overrides?.CalleeDepth ?? preset.CalleeDepth,
        selection.Overrides?.RelationDepth ?? preset.RelationDepth);
}
