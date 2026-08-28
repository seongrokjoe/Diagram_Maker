using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed record DiagramProjectionResult(
    IReadOnlyList<DiagramArtifact> Artifacts,
    IReadOnlyList<DiagramAvailability> Availability);

public sealed class DiagramProjectionService
{
    private const int DisplayNodeLimit = 80;
    private const int DisplayEdgeLimit = 120;
    private static readonly string[] SupportedTypes = ["flowchart", "class", "sequence", "state"];

    public DiagramProjectionResult Build(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlyList<string>? requestedTypes,
        int callerDepth,
        int calleeDepth,
        bool contextFilesTruncated)
    {
        var types = (requestedTypes is null || requestedTypes.Count == 0 ? ["flowchart"] : requestedTypes)
            .Select(NormalizeType)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var changes = BuildChangeMap(graph);
        var selected = SelectImpact(graph, changes.Keys, callerDepth, calleeDepth);
        var availability = new List<DiagramAvailability>();
        var artifacts = new List<DiagramArtifact>();

        foreach (var type in types)
        {
            if (type == "state")
            {
                availability.Add(new DiagramAvailability(type, false,
                    "정적 분석 결과에 명시적인 상태 전이 근거가 없어 생성하지 않았습니다."));
                continue;
            }

            var ir = type switch
            {
                "class" => BuildClass(repositoryName, graph, comparison, selected, changes, contextFilesTruncated),
                "sequence" => BuildSequence(repositoryName, graph, comparison, selected, changes, contextFilesTruncated),
                _ => BuildFlow(repositoryName, graph, comparison, selected, changes, contextFilesTruncated)
            };
            if (ir.Nodes.Count == 0)
            {
                availability.Add(new DiagramAvailability(type, false, "변경된 심볼이 없어 다이어그램을 생성할 수 없습니다."));
                continue;
            }

            artifacts.Add(new DiagramArtifact(Guid.NewGuid(), type, 1, ir, string.Empty, DateTimeOffset.UtcNow));
            availability.Add(new DiagramAvailability(type, true, null));
        }

        return new DiagramProjectionResult(artifacts, availability);
    }

    public static bool IsSupported(string type) => SupportedTypes.Contains(NormalizeType(type), StringComparer.Ordinal);

    private static DiagramIr BuildFlow(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes,
        bool contextFilesTruncated)
    {
        var nodes = CreateNodes(graph, comparison, selected, changes);
        var edges = CreateEdges(graph, selected, changes, sequence: false);
        return CreateIr("flowchart", repositoryName, comparison, nodes, edges, contextFilesTruncated,
            "변경 심볼 중심의 caller/callee 영향도 흐름입니다.");
    }

    private static DiagramIr BuildSequence(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes,
        bool contextFilesTruncated)
    {
        var callable = selected.Where(id => graph.Identities.FirstOrDefault(identity => identity.Id == id)?.Kind is "method" or "constructor" or "function").ToHashSet(StringComparer.Ordinal);
        if (callable.Count == 0) callable = selected.ToHashSet(StringComparer.Ordinal);
        var nodes = CreateNodes(graph, comparison, callable, changes);
        var edges = CreateEdges(graph, callable, changes, sequence: true)
            .Where(edge => edge.Type.Equals("calls", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return CreateIr("sequence", repositoryName, comparison, nodes, edges, contextFilesTruncated,
            "정적 호출 관계를 정렬한 시퀀스이며 실제 런타임 실행 순서를 의미하지 않습니다.");
    }

    private static DiagramIr BuildClass(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes,
        bool contextFilesTruncated)
    {
        var versions = CurrentVersions(graph, comparison);
        var typeVersions = versions.Where(version => IsType(graph, version.IdentityId)).ToArray();
        var ownerByIdentity = BuildOwners(versions, typeVersions);
        var owners = selected.Select(id => ownerByIdentity.GetValueOrDefault(id, id)).ToHashSet(StringComparer.Ordinal);
        var nodes = CreateNodes(graph, comparison, owners, changes, ownerByIdentity, versions);
        var edges = graph.Edges
            .Where(edge => selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(edge => edge.Type.Equals("inherits", StringComparison.OrdinalIgnoreCase) || edge.Type.Equals("calls", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge with
            {
                FromIdentityId = ownerByIdentity.GetValueOrDefault(edge.FromIdentityId, edge.FromIdentityId),
                ToIdentityId = ownerByIdentity.GetValueOrDefault(edge.ToIdentityId, edge.ToIdentityId)
            })
            .Where(edge => edge.FromIdentityId != edge.ToIdentityId && owners.Contains(edge.FromIdentityId) && owners.Contains(edge.ToIdentityId))
            .DistinctBy(edge => $"{edge.FromIdentityId}:{edge.ToIdentityId}:{edge.Type}")
            .Take(DisplayEdgeLimit)
            .Select(edge => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "unchanged", edge.Confidence, edge.EvidenceIds))
            .ToArray();
        return CreateIr("class", repositoryName, comparison, nodes, edges, contextFilesTruncated,
            "변경 메서드는 소유 클래스에 축약되어 표시됩니다.");
    }

    private static DiagramIr CreateIr(
        string type,
        string repositoryName,
        GitComparison comparison,
        IReadOnlyList<DiagramNode> nodes,
        IReadOnlyList<DiagramEdge> edges,
        bool contextFilesTruncated,
        string description)
    {
        var notes = new List<string>
        {
            description,
            "변경 심볼은 색상으로 구분되며 관계 근거는 Evidence에서 확인할 수 있습니다."
        };
        if (contextFilesTruncated) notes.Add("참조 컨텍스트가 제한되어 일부 외부 caller/callee가 누락될 수 있습니다.");
        if (nodes.Count >= DisplayNodeLimit || edges.Count >= DisplayEdgeLimit)
            notes.Add($"표시는 최대 {DisplayNodeLimit}개 노드와 {DisplayEdgeLimit}개 관계로 제한됩니다.");
        return new DiagramIr(type, $"{repositoryName}: {comparison.BaseSha[..8]} → {comparison.TargetSha[..8]}", nodes, edges, notes,
            [comparison.BaseSha, comparison.TargetSha]);
    }

    private static IReadOnlyList<DiagramNode> CreateNodes(
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes,
        IReadOnlyDictionary<string, string>? ownerByIdentity = null,
        IReadOnlyList<SymbolVersion>? versions = null)
    {
        versions ??= CurrentVersions(graph, comparison);
        return selected
            .Select(identityId => graph.Identities.FirstOrDefault(identity => identity.Id == identityId))
            .Where(identity => identity is not null)
            .Select(identity =>
            {
                var actual = identity!;
                var version = versions.FirstOrDefault(candidate => candidate.IdentityId == actual.Id)
                              ?? graph.Versions.FirstOrDefault(candidate => candidate.IdentityId == actual.Id);
                if (version is null) return null;
                var changedMethods = ownerByIdentity is null
                    ? string.Empty
                    : graph.Versions.Where(candidate => candidate.IdentityId != actual.Id && ownerByIdentity.GetValueOrDefault(candidate.IdentityId) == actual.Id && changes.ContainsKey(candidate.IdentityId))
                        .Select(candidate => candidate.QualifiedName.Split('.').Last())
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Take(8)
                        .ToArray() is { Length: > 0 } names ? $" (변경: {string.Join(", ", names)})" : string.Empty;
                var evidence = graph.Evidence.Where(item => item.RevisionSha == version.RevisionSha && item.FilePath == version.FilePath && item.StartLine == version.StartLine).Select(item => item.Id).ToArray();
                var label = version.QualifiedName + changedMethods;
                return new DiagramNode(actual.Id, label, actual.Kind, Path.GetDirectoryName(version.FilePath), changes.GetValueOrDefault(actual.Id, "unchanged"), evidence.Length == 0 ? Confidence.Inferred : Confidence.Exact, evidence);
            })
            .Where(node => node is not null)
            .Take(DisplayNodeLimit)
            .Cast<DiagramNode>()
            .ToArray();
    }

    private static IReadOnlyList<DiagramEdge> CreateEdges(VersionedGraph graph, IReadOnlySet<string> selected, IReadOnlyDictionary<string, string> changes, bool sequence)
    {
        return graph.Edges
            .Where(edge => selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(edge => edge.Type is "calls" or "inherits")
            .OrderBy(edge => changes.ContainsKey(edge.FromIdentityId) || changes.ContainsKey(edge.ToIdentityId) ? 0 : 1)
            .ThenBy(edge => edge.FromIdentityId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToIdentityId, StringComparer.Ordinal)
            .Take(DisplayEdgeLimit)
            .Select((edge, index) => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "unchanged", edge.Confidence, edge.EvidenceIds, sequence ? index + 1 : edge.SequenceIndex))
            .ToArray();
    }

    private static HashSet<string> SelectImpact(VersionedGraph graph, IEnumerable<string> roots, int callerDepth, int calleeDepth)
    {
        var selected = roots.ToHashSet(StringComparer.Ordinal);
        var distances = selected.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var queue = new Queue<string>(selected);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var distance = distances[current];
            foreach (var edge in graph.Edges.Where(edge => edge.Type == "calls"))
            {
                var candidate = edge.FromIdentityId == current && distance < calleeDepth ? edge.ToIdentityId
                    : edge.ToIdentityId == current && distance < callerDepth ? edge.FromIdentityId
                    : null;
                if (candidate is null || !distances.TryAdd(candidate, distance + 1)) continue;
                selected.Add(candidate);
                queue.Enqueue(candidate);
            }
        }
        return selected;
    }

    private static Dictionary<string, string> BuildChangeMap(VersionedGraph graph)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in graph.Changes)
        {
            var id = graph.Versions.FirstOrDefault(version => version.Id == change.AfterSymbolVersionId)?.IdentityId
                     ?? graph.Versions.FirstOrDefault(version => version.Id == change.BeforeSymbolVersionId)?.IdentityId;
            if (id is not null) result[id] = change.Type switch
            {
                SymbolChangeKind.AddSymbol => "added",
                SymbolChangeKind.RemoveSymbol => "deleted",
                _ => "modified"
            };
        }
        return result;
    }

    private static IReadOnlyList<SymbolVersion> CurrentVersions(VersionedGraph graph, GitComparison comparison) =>
        graph.Versions.Where(version => version.RevisionSha == comparison.TargetSha || !graph.Versions.Any(candidate => candidate.IdentityId == version.IdentityId && candidate.RevisionSha == comparison.TargetSha)).ToArray();

    private static Dictionary<string, string> BuildOwners(IReadOnlyList<SymbolVersion> versions, IReadOnlyList<SymbolVersion> typeVersions)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var owner = typeVersions.Where(type => version.QualifiedName.StartsWith(type.QualifiedName + ".", StringComparison.Ordinal))
                .OrderByDescending(type => type.QualifiedName.Length)
                .FirstOrDefault();
            result[version.IdentityId] = owner?.IdentityId ?? version.IdentityId;
        }
        return result;
    }

    private static bool IsType(VersionedGraph graph, string identityId) =>
        graph.Identities.FirstOrDefault(identity => identity.Id == identityId)?.Kind is { } kind &&
        (kind.Contains("type", StringComparison.OrdinalIgnoreCase) || kind.Contains("class", StringComparison.OrdinalIgnoreCase) || kind.Contains("interface", StringComparison.OrdinalIgnoreCase) || kind.Contains("struct", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "flow" or "dependency" or "component" => "flowchart",
        "classdiagram" => "class",
        _ => type.Trim().ToLowerInvariant()
    };
}
