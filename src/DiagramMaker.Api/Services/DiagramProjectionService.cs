using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed record DiagramProjectionResult(IReadOnlyList<DiagramArtifact> Artifacts, IReadOnlyList<DiagramAvailability> Availability);

public enum DiagramRevisionSide
{
    Combined,
    Base,
    Target
}

public sealed class DiagramProjectionService
{
    private const int DisplayNodeLimit = 80;
    private const int DisplayEdgeLimit = 120;
    private static readonly string[] SupportedTypes = ["flowchart", "class", "sequence", "code-relation", "state"];

    public DiagramProjectionResult Build(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlyList<string>? requestedTypes,
        int callerDepth, int calleeDepth, bool contextFilesTruncated,
        IReadOnlySet<string>? selectedChangeIds = null, DiagramPreset? preset = null, DiagramStyleOverrides? overrides = null,
        bool focusOnChanges = false, DiagramRevisionSide revisionSide = DiagramRevisionSide.Combined)
    {
        var types = (requestedTypes is null || requestedTypes.Count == 0 ? ["flowchart"] : requestedTypes)
            .Select(NormalizeType).Distinct(StringComparer.Ordinal).ToArray();
        var changes = BuildChangeMap(graph, selectedChangeIds);
        callerDepth = Math.Clamp(overrides?.CallerDepth ?? preset?.CallerDepth ?? callerDepth, 0, 3);
        calleeDepth = Math.Clamp(overrides?.CalleeDepth ?? preset?.CalleeDepth ?? calleeDepth, 0, 3);
        var maximumNodes = ResolveMaximum(overrides?.DetailLevel, preset?.MaximumNodes ?? DisplayNodeLimit, true);
        var maximumEdges = ResolveMaximum(overrides?.DetailLevel, preset?.MaximumEdges ?? DisplayEdgeLimit, false);
        var direction = NormalizeDirection(overrides?.Direction ?? preset?.Direction);
        var selected = SelectImpact(graph, comparison, changes.Keys, callerDepth, calleeDepth, revisionSide);
        var availability = new List<DiagramAvailability>();
        var artifacts = new List<DiagramArtifact>();

        foreach (var type in types)
        {
            if (type == "state")
            {
                availability.Add(new DiagramAvailability(type, false, "정적 분석 결과에 명시적인 상태 전이 근거가 없어 생성하지 않습니다."));
                continue;
            }
            var ir = type switch
            {
                "class" => BuildClass(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges, revisionSide),
                "sequence" => BuildSequence(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges, revisionSide),
                "code-relation" => BuildCodeRelation(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges, revisionSide),
                _ => BuildFlow(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges, revisionSide)
            };
            if (ir.Nodes.Count == 0)
            {
                availability.Add(new DiagramAvailability(type, false, "선택한 변경 심볼이 없어 다이어그램을 생성할 수 없습니다."));
                continue;
            }
            artifacts.Add(new DiagramArtifact(Guid.NewGuid(), type, 1, ir, string.Empty, DateTimeOffset.UtcNow));
            availability.Add(new DiagramAvailability(type, true, null));
        }
        return new DiagramProjectionResult(artifacts, availability);
    }

    public static bool IsSupported(string type) => SupportedTypes.Contains(NormalizeType(type), StringComparer.Ordinal);

    private static DiagramIr BuildFlow(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges, DiagramRevisionSide revisionSide)
    {
        var selectedFlows = (graph.ControlFlows ?? [])
            .Where(flow => changes.ContainsKey(flow.IdentityId))
            .Where(flow => revisionSide == DiagramRevisionSide.Combined ||
                           flow.RevisionSha == RevisionSha(comparison, revisionSide))
            .GroupBy(static flow => $"{flow.IdentityId}\0{flow.RevisionSha}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(flow => flow.RevisionSha == comparison.TargetSha)
                .ThenByDescending(static flow => flow.Nodes.Count)
                .ThenBy(static flow => flow.FilePath, StringComparer.Ordinal)
                .First())
            .ToArray();
        if (selectedFlows.Length == 0)
        {
            var fallbackNodes = CreateNodes(graph, comparison, selected, changes, maximumNodes: maxNodes, revisionSide: revisionSide);
            var fallbackEdges = KeepEdgesBetweenNodes(CreateEdges(graph, comparison, selected, changes, false, maxEdges, revisionSide), fallbackNodes, maxEdges);
            if (focusOnChanges) (fallbackNodes, fallbackEdges) = FocusDiagram(fallbackNodes, fallbackEdges);
            return CreateIr("flowchart", repositoryName, comparison, fallbackNodes, fallbackEdges, truncated, maxNodes, maxEdges, direction, revisionSide,
                "제어 흐름 근거가 없는 언어이므로 변경 심볼 중심의 호출 영향도를 표시합니다.");
        }

        var versions = CurrentVersions(graph, comparison, revisionSide).ToDictionary(static version => version.IdentityId, StringComparer.Ordinal);
        var nodes = new List<DiagramNode>();
        var edges = new List<DiagramEdge>();
        foreach (var flow in selectedFlows.OrderBy(static item => item.IdentityId, StringComparer.Ordinal))
        {
            var group = versions.GetValueOrDefault(flow.IdentityId)?.QualifiedName ?? flow.IdentityId;
            var isBaseGhostFlow = revisionSide == DiagramRevisionSide.Combined && flow.RevisionSha == comparison.BaseSha &&
                                  selectedFlows.Any(candidate => candidate.IdentityId == flow.IdentityId && candidate.RevisionSha == comparison.TargetSha);
            var visibleFlowNodes = flow.Nodes
                .Where(node => !isBaseGhostFlow || MarkerForControl(graph, comparison, flow, node, changes, revisionSide)?.Kind == DiagramChangeKind.Deleted)
                .ToArray();
            var visibleIds = visibleFlowNodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
            nodes.AddRange(visibleFlowNodes.Select(node => new DiagramNode(
                node.Id, node.Label, node.Kind, group, MarkerForControl(graph, comparison, flow, node, changes, revisionSide)?.Kind.ToString().ToLowerInvariant() ?? "unchanged",
                Confidence.Exact, node.EvidenceIds, ShapeForControl(node.Kind),
                node.CallTargetIdentityId is null ? null : [LabelForIdentity(graph, comparison, node.CallTargetIdentityId)],
                MarkerForControl(graph, comparison, flow, node, changes, revisionSide))));
            edges.AddRange(flow.Edges.Where(edge => visibleIds.Contains(edge.SourceId) && visibleIds.Contains(edge.TargetId)).Select((edge, index) => new DiagramEdge(
                StableIds.Create(flow.IdentityId, edge.SourceId, edge.TargetId, edge.Type, index), edge.SourceId, edge.TargetId,
                edge.Type, edge.Label, "unchanged", Confidence.Exact,
                flow.Nodes.FirstOrDefault(node => node.Id == edge.SourceId)?.EvidenceIds ?? [],
                ChangeMarker: nodes.LastOrDefault(node => node.Id == edge.SourceId)?.ChangeMarker)));
        }
        var limitedNodes = nodes.DistinctBy(static node => node.Id).Take(maxNodes).ToArray();
        var limitedEdges = KeepEdgesBetweenNodes(edges.DistinctBy(static edge => edge.Id), limitedNodes, maxEdges);
        if (focusOnChanges) (limitedNodes, limitedEdges) = FocusDiagram(limitedNodes, limitedEdges);
        return CreateIr("flowchart", repositoryName, comparison, limitedNodes, limitedEdges, truncated, maxNodes, maxEdges, direction, revisionSide,
            "선택한 C++ 변경 메서드의 조건, 반복, 호출 및 리턴을 정적 구문 순서로 표시합니다.");
    }

    private static DiagramIr BuildSequence(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges, DiagramRevisionSide revisionSide)
    {
        var callable = selected.Where(id => graph.Identities.FirstOrDefault(identity => identity.Id == id)?.Kind is "method" or "constructor" or "function" or "class" or "type")
            .ToHashSet(StringComparer.Ordinal);
        if (callable.Count == 0) callable = selected.ToHashSet(StringComparer.Ordinal);
        var nodes = CreateNodes(graph, comparison, callable, changes, maximumNodes: maxNodes, revisionSide: revisionSide);
        var edges = KeepEdgesBetweenNodes(CreateEdges(graph, comparison, callable, changes, true, maxEdges, revisionSide), nodes, maxEdges)
            .Where(static edge => edge.Type.Equals("calls", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (focusOnChanges) (nodes, edges) = FocusDiagram(nodes, edges);
        return CreateIr("sequence", repositoryName, comparison, nodes, edges, truncated, maxNodes, maxEdges, direction, revisionSide,
            "정적 호출 위치와 확인된 조건·반복 범위를 표시하며 실제 런타임 실행 경로를 의미하지 않습니다.");
    }

    private static DiagramIr BuildClass(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges, DiagramRevisionSide revisionSide)
    {
        var versions = CurrentVersions(graph, comparison, revisionSide);
        var typeVersions = versions.Where(version => IsType(graph, version.IdentityId)).ToArray();
        var owners = BuildOwners(versions, typeVersions);
        var selectedOwners = selected.Select(id => owners.GetValueOrDefault(id, id)).ToHashSet(StringComparer.Ordinal);
        var nodes = CreateNodes(graph, comparison, selectedOwners, changes, owners, versions, maxNodes);
        var edges = CreateEdges(graph, comparison, selected, changes, false, maxEdges, revisionSide)
            .Select(edge => edge with
            {
                SourceId = owners.GetValueOrDefault(edge.SourceId, edge.SourceId),
                TargetId = owners.GetValueOrDefault(edge.TargetId, edge.TargetId),
                Label = edge.IsIndirect ? $"간접 API {edge.ViaApi}" : edge.Label
            })
            .Where(edge => edge.SourceId != edge.TargetId && selectedOwners.Contains(edge.SourceId) && selectedOwners.Contains(edge.TargetId))
            .ToArray();
        edges = CollapseLogicalEdges(edges, maxEdges);
        edges = KeepEdgesBetweenNodes(edges, nodes, maxEdges);
        if (focusOnChanges) (nodes, edges) = FocusDiagram(nodes, edges);
        return CreateIr("class", repositoryName, comparison, nodes, edges, truncated, maxNodes, maxEdges, direction, revisionSide,
            "변경 메서드를 소유 클래스에 축약하고 상속 및 호출 의존 방향을 표시합니다.");
    }

    private static DiagramIr BuildCodeRelation(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges, DiagramRevisionSide revisionSide)
    {
        var versions = CurrentVersions(graph, comparison, revisionSide);
        var owners = BuildOwners(versions, versions.Where(version => IsType(graph, version.IdentityId)).ToArray());
        var nodes = CreateNodes(graph, comparison, selected, changes, maximumNodes: maxNodes, revisionSide: revisionSide)
            .Select(node => node with
            {
                Label = LastQualifiedPart(node.Label),
                Group = OwnerLabel(versions, owners.GetValueOrDefault(node.Id, node.Id)),
                Shape = IsType(graph, node.Id) ? "type" : "method"
            }).ToArray();
        var edges = KeepEdgesBetweenNodes(CreateEdges(graph, comparison, selected, changes, false, maxEdges, revisionSide), nodes, maxEdges)
            .Select(edge => edge with { Label = edge.IsIndirect ? $"간접 API: {edge.ViaApi}" : edge.Label }).ToArray();
        edges = CollapseLogicalEdges(edges, maxEdges);
        if (focusOnChanges) (nodes, edges) = FocusDiagram(nodes, edges);
        return CreateIr("code-relation", repositoryName, comparison, nodes, edges, truncated, maxNodes, maxEdges, direction, revisionSide,
            "클래스별 카드 안에 선택 메서드와 직접 관련 메서드를 배치한 코드 관계도입니다.");
    }

    private static DiagramIr CreateIr(
        string type, string repositoryName, GitComparison comparison, IReadOnlyList<DiagramNode> nodes,
        IReadOnlyList<DiagramEdge> edges, bool truncated, int maxNodes, int maxEdges, string direction,
        DiagramRevisionSide revisionSide, string description)
    {
        var notes = new List<string> { description, "변경 심볼은 색상으로 구분하며 관계 근거는 Evidence에서 확인할 수 있습니다." };
        if (truncated) notes.Add("참조 문맥이 제한되어 일부 관계가 누락될 수 있습니다.");
        if (nodes.Count >= maxNodes || edges.Count >= maxEdges) notes.Add($"표시는 최대 {maxNodes}개 노드와 {maxEdges}개 관계로 제한합니다.");
        var title = revisionSide switch
        {
            DiagramRevisionSide.Base => $"{repositoryName}: Base {comparison.BaseSha[..8]}",
            DiagramRevisionSide.Target => $"{repositoryName}: Target {comparison.TargetSha[..8]}",
            _ => $"{repositoryName}: {comparison.BaseSha[..8]} → {comparison.TargetSha[..8]}"
        };
        return new DiagramIr(type, title, nodes, edges,
            notes, [comparison.BaseSha, comparison.TargetSha], direction);
    }

    private static IReadOnlyList<DiagramNode> CreateNodes(
        VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected, IReadOnlyDictionary<string, string> changes,
        IReadOnlyDictionary<string, string>? ownerByIdentity = null, IReadOnlyList<SymbolVersion>? versions = null,
        int maximumNodes = DisplayNodeLimit, DiagramRevisionSide revisionSide = DiagramRevisionSide.Combined)
    {
        versions ??= CurrentVersions(graph, comparison, revisionSide);
        return selected.OrderByDescending(changes.ContainsKey).ThenBy(static id => id, StringComparer.Ordinal)
            .Select(id => graph.Identities.FirstOrDefault(identity => identity.Id == id)).Where(static identity => identity is not null)
            .Select(identity =>
            {
                var actual = identity!;
                var version = versions.FirstOrDefault(candidate => candidate.IdentityId == actual.Id);
                if (version is null) return null;
                var hasChangedMembers = ownerByIdentity is not null && graph.Versions
                    .Where(candidate => candidate.IdentityId != actual.Id && ownerByIdentity.GetValueOrDefault(candidate.IdentityId) == actual.Id && changes.ContainsKey(candidate.IdentityId))
                    .Any();
                var nodeStatus = changes.GetValueOrDefault(actual.Id,
                    hasChangedMembers ? "modified" : "unchanged");
                var evidence = graph.Evidence.Where(item => item.RevisionSha == version.RevisionSha && item.FilePath == version.FilePath && item.StartLine == version.StartLine)
                    .Select(static item => item.Id).ToArray();
                return new DiagramNode(actual.Id, version.QualifiedName, actual.Kind, Path.GetDirectoryName(version.FilePath),
                    nodeStatus, evidence.Length == 0 ? Confidence.Inferred : Confidence.Exact, evidence,
                    IsType(graph, actual.Id) ? "type" : "method", [version.Signature],
                    MarkerForSymbol(version, nodeStatus, evidence));
            }).Where(static node => node is not null).Take(maximumNodes).Cast<DiagramNode>().ToArray();
    }

    private static DiagramEdge[] KeepEdgesBetweenNodes(IEnumerable<DiagramEdge> edges, IReadOnlyList<DiagramNode> nodes, int maxEdges)
    {
        var nodeIds = nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        return edges.Where(edge => nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId)).Take(maxEdges).ToArray();
    }

    private static DiagramEdge[] CollapseLogicalEdges(IEnumerable<DiagramEdge> edges, int maxEdges) => edges
        .GroupBy(static edge => $"{edge.SourceId}\0{edge.TargetId}\0{edge.Type}\0{edge.IsIndirect}\0{edge.ViaApi}", StringComparer.Ordinal)
        .Select(group =>
        {
            var values = group.OrderBy(static edge => edge.SequenceIndex ?? int.MaxValue).ThenBy(static edge => edge.Id, StringComparer.Ordinal).ToArray();
            var representative = values.FirstOrDefault(static edge => !edge.Status.Equals("deleted", StringComparison.OrdinalIgnoreCase)) ?? values[0];
            var evidence = values.SelectMany(static edge => edge.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
            var markers = values.Select(static edge => edge.ChangeMarker).Where(static marker => marker is not null).Cast<DiagramChangeMarker>().ToArray();
            DiagramChangeMarker? marker = null;
            if (markers.Length > 0)
            {
                var kind = markers.Any(static item => item.Kind == DiagramChangeKind.Modified) ||
                           markers.Select(static item => item.Kind).Distinct().Count() > 1 ||
                           values.Any(static edge => edge.ChangeMarker is null)
                    ? DiagramChangeKind.Modified
                    : markers[0].Kind;
                var basis = markers.FirstOrDefault(item => item.Kind == kind) ?? markers[0];
                marker = basis with { Kind = kind, EvidenceIds = evidence };
            }
            return representative with
            {
                Id = StableIds.Create("logical-diagram-edge", group.Key),
                Status = marker?.Kind.ToString().ToLowerInvariant() ?? "unchanged",
                Confidence = values.Any(static edge => edge.Confidence == Confidence.Exact) ? Confidence.Exact : representative.Confidence,
                EvidenceIds = evidence,
                SequenceIndex = values.Select(static edge => edge.SequenceIndex).Where(static index => index is not null).Min(),
                ChangeMarker = marker
            };
        })
        .Take(maxEdges)
        .ToArray();

    private static (DiagramNode[] Nodes, DiagramEdge[] Edges) FocusDiagram(
        IReadOnlyList<DiagramNode> nodes, IReadOnlyList<DiagramEdge> edges)
    {
        var changed = nodes.Where(static node => node.ChangeMarker is not null)
            .Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var emphasizedEdges = edges.Where(edge => edge.ChangeMarker is not null || changed.Contains(edge.SourceId) || changed.Contains(edge.TargetId)).ToArray();
        foreach (var edge in emphasizedEdges)
        {
            changed.Add(edge.SourceId);
            changed.Add(edge.TargetId);
        }

        if (changed.Count == 0) return (nodes.ToArray(), edges.ToArray());
        var changedGroups = nodes.Where(node => changed.Contains(node.Id)).Select(static node => node.Group)
            .Where(static group => group is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => changedGroups.Contains(node.Group) && node.Shape is "terminal" or "decision"))
            changed.Add(node.Id);

        var focusedNodes = nodes.Where(node => changed.Contains(node.Id)).ToArray();
        var focusedEdges = edges.Where(edge => changed.Contains(edge.SourceId) && changed.Contains(edge.TargetId) &&
                                               (edge.ChangeMarker is not null || emphasizedEdges.Contains(edge) ||
                                                focusedNodes.Any(node => node.Id == edge.SourceId && node.Shape is "terminal" or "decision")))
            .ToArray();
        return (focusedNodes, focusedEdges);
    }

    private static IReadOnlyList<DiagramEdge> CreateEdges(
        VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected, IReadOnlyDictionary<string, string> changes,
        bool sequence, int maxEdges, DiagramRevisionSide revisionSide)
    {
        var target = EdgesForRevision(graph, comparison, revisionSide).Where(edge => selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(static edge => edge.Type is "calls" or "inherits")
            .OrderBy(edge => changes.ContainsKey(edge.FromIdentityId) || changes.ContainsKey(edge.ToIdentityId) ? 0 : 1)
            .ThenBy(static edge => edge.SequenceIndex ?? int.MaxValue).ThenBy(static edge => edge.FromIdentityId, StringComparer.Ordinal)
            .ThenBy(static edge => edge.ToIdentityId, StringComparer.Ordinal)
            .Select((edge, index) => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "unchanged",
                edge.Confidence, edge.EvidenceIds, sequence ? edge.SequenceIndex ?? index + 1 : edge.SequenceIndex,
                edge.IsIndirect, edge.ViaApi, edge.ControlPath, MarkerForGraphEdge(graph, comparison, edge, revisionSide)));
        if (revisionSide != DiagramRevisionSide.Combined) return target.Take(maxEdges).ToArray();
        var deleted = graph.Edges
            .Where(edge => edge.RevisionSha == comparison.BaseSha && selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(static edge => edge.Type is "calls" or "inherits")
            .Where(edge => MarkerForGraphEdge(graph, comparison, edge, revisionSide)?.Kind == DiagramChangeKind.Deleted)
            .Select(edge => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "deleted",
                edge.Confidence, edge.EvidenceIds, sequence ? edge.SequenceIndex : null, edge.IsIndirect, edge.ViaApi,
                edge.ControlPath, MarkerForGraphEdge(graph, comparison, edge, revisionSide)));
        return target.Concat(deleted).DistinctBy(static edge => edge.Id).Take(maxEdges).ToArray();
    }

    private static HashSet<string> SelectImpact(
        VersionedGraph graph, GitComparison comparison, IEnumerable<string> roots, int callerDepth, int calleeDepth,
        DiagramRevisionSide revisionSide)
    {
        var available = CurrentVersions(graph, comparison, revisionSide)
            .Select(static version => version.IdentityId).ToHashSet(StringComparer.Ordinal);
        var selected = roots.Where(available.Contains).ToHashSet(StringComparer.Ordinal);
        var traversalEdges = EdgesForRevision(graph, comparison, revisionSide)
            .Concat(revisionSide == DiagramRevisionSide.Combined
                ? graph.Edges.Where(edge => edge.RevisionSha == comparison.BaseSha &&
                    MarkerForGraphEdge(graph, comparison, edge, revisionSide)?.Kind == DiagramChangeKind.Deleted)
                : [])
            .Where(static edge => edge.Type == "calls")
            .DistinctBy(static edge => edge.Id)
            .ToArray();
        var distances = selected.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var queue = new Queue<string>(selected);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var distance = distances[current];
            foreach (var edge in traversalEdges)
            {
                var candidate = edge.FromIdentityId == current && distance < calleeDepth ? edge.ToIdentityId
                    : edge.ToIdentityId == current && distance < callerDepth ? edge.FromIdentityId : null;
                if (candidate is null || !distances.TryAdd(candidate, distance + 1)) continue;
                selected.Add(candidate);
                queue.Enqueue(candidate);
            }
        }
        return selected;
    }

    private static IEnumerable<GraphEdge> EdgesForRevision(
        VersionedGraph graph, GitComparison comparison, DiagramRevisionSide revisionSide)
    {
        var revisionSha = RevisionSha(comparison, revisionSide);
        return graph.Edges.Where(edge => edge.RevisionSha is null || edge.RevisionSha == revisionSha);
    }

    private static string RevisionSha(GitComparison comparison, DiagramRevisionSide revisionSide) =>
        revisionSide == DiagramRevisionSide.Base ? comparison.BaseSha : comparison.TargetSha;

    private static DiagramChangeMarker? MarkerForSymbol(SymbolVersion version, string status, IReadOnlyList<string> evidenceIds)
    {
        var kind = status switch
        {
            "added" => DiagramChangeKind.Added,
            "modified" => DiagramChangeKind.Modified,
            "deleted" => DiagramChangeKind.Deleted,
            _ => (DiagramChangeKind?)null
        };
        return kind is null ? null : new DiagramChangeMarker(
            kind.Value, DiagramChangePrecision.Symbol, version.FilePath, version.StartLine, version.EndLine, evidenceIds);
    }

    private static DiagramChangeMarker? MarkerForControl(
        VersionedGraph graph,
        GitComparison comparison,
        MethodControlFlow flow,
        ControlFlowNode node,
        IReadOnlyDictionary<string, string> changes,
        DiagramRevisionSide revisionSide)
    {
        if (flow.RevisionSha is null || flow.FilePath is null ||
            !OverlapsChangedRange(comparison, flow.RevisionSha, flow.FilePath, node.StartLine, node.EndLine)) return null;
        DiagramChangeKind kind;
        if (flow.RevisionSha == comparison.BaseSha)
        {
            var baseRank = CompatibleControlNodes(graph, flow.IdentityId, comparison.BaseSha, node.Kind)
                .FindIndex(candidate => candidate.Id == node.Id);
            var targetCount = CompatibleControlNodes(graph, flow.IdentityId, comparison.TargetSha, node.Kind).Count;
            if (baseRank < 0) return null;
            if (baseRank >= targetCount) kind = DiagramChangeKind.Deleted;
            else if (revisionSide == DiagramRevisionSide.Base && changes.GetValueOrDefault(flow.IdentityId) == "modified")
                kind = DiagramChangeKind.Modified;
            else return null;
        }
        else
        {
            var targetRank = CompatibleControlNodes(graph, flow.IdentityId, comparison.TargetSha, node.Kind)
                .FindIndex(candidate => candidate.Id == node.Id);
            var baseCount = CompatibleControlNodes(graph, flow.IdentityId, comparison.BaseSha, node.Kind).Count;
            kind = changes.GetValueOrDefault(flow.IdentityId) == "added" || targetRank >= baseCount
                ? DiagramChangeKind.Added
                : DiagramChangeKind.Modified;
        }
        return new DiagramChangeMarker(kind, DiagramChangePrecision.Exact, flow.FilePath,
            node.StartLine, node.EndLine, node.EvidenceIds);
    }

    private static List<ControlFlowNode> CompatibleControlNodes(
        VersionedGraph graph, string identityId, string revisionSha, string kind) => (graph.ControlFlows ?? [])
        .Where(flow => flow.IdentityId == identityId && flow.RevisionSha == revisionSha)
        .SelectMany(static flow => flow.Nodes)
        .Where(node => node.Kind == kind)
        .DistinctBy(static node => node.Id)
        .OrderBy(static node => node.StartLine)
        .ThenBy(static node => node.EndLine)
        .ThenBy(static node => node.Id, StringComparer.Ordinal)
        .ToList();

    private static DiagramChangeMarker? MarkerForGraphEdge(
        VersionedGraph graph, GitComparison comparison, GraphEdge edge, DiagramRevisionSide revisionSide)
    {
        if (edge.RevisionSha is null || edge.FilePath is null || edge.StartLine is null || edge.EndLine is null ||
            !OverlapsChangedRange(comparison, edge.RevisionSha, edge.FilePath, edge.StartLine.Value, edge.EndLine.Value)) return null;
        if (edge.RevisionSha == comparison.BaseSha)
        {
            var baseRank = CompatibleEdges(graph, edge, comparison.BaseSha).FindIndex(candidate => candidate.Id == edge.Id);
            var targetCount = CompatibleEdges(graph, edge, comparison.TargetSha).Count;
            if (baseRank < 0) return null;
            var kind = baseRank >= targetCount ? DiagramChangeKind.Deleted
                : revisionSide == DiagramRevisionSide.Base ? DiagramChangeKind.Modified
                : (DiagramChangeKind?)null;
            return kind is null ? null : new DiagramChangeMarker(kind.Value, DiagramChangePrecision.Exact,
                edge.FilePath, edge.StartLine, edge.EndLine, edge.EvidenceIds);
        }
        var targetRank = CompatibleEdges(graph, edge, comparison.TargetSha).FindIndex(candidate => candidate.Id == edge.Id);
        var baseCount = CompatibleEdges(graph, edge, comparison.BaseSha).Count;
        return new DiagramChangeMarker(targetRank >= 0 && targetRank < baseCount ? DiagramChangeKind.Modified : DiagramChangeKind.Added,
            DiagramChangePrecision.Exact, edge.FilePath, edge.StartLine, edge.EndLine, edge.EvidenceIds);
    }

    private static List<GraphEdge> CompatibleEdges(VersionedGraph graph, GraphEdge edge, string revisionSha) => graph.Edges
        .Where(candidate => candidate.RevisionSha == revisionSha &&
            candidate.FromIdentityId == edge.FromIdentityId && candidate.ToIdentityId == edge.ToIdentityId &&
            candidate.Type == edge.Type && candidate.IsIndirect == edge.IsIndirect && candidate.ViaApi == edge.ViaApi)
        .OrderBy(static candidate => candidate.StartLine ?? int.MaxValue)
        .ThenBy(static candidate => candidate.SequenceIndex ?? int.MaxValue)
        .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
        .ToList();

    private static bool OverlapsChangedRange(
        GitComparison comparison, string revisionSha, string filePath, int startLine, int endLine)
    {
        var targetSide = revisionSha == comparison.TargetSha;
        var file = comparison.Files.FirstOrDefault(candidate =>
            (targetSide ? candidate.Path : candidate.PreviousPath ?? candidate.Path)
                .Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (file is null) return false;
        return file.Hunks.SelectMany(static hunk => hunk.ChangedRanges ?? [])
            .Any(range =>
            {
                var changedStart = targetSide ? range.NewStartLine : range.OldStartLine;
                var count = targetSide ? range.NewLineCount : range.OldLineCount;
                return changedStart is { } line && count > 0 && startLine <= line + count - 1 && endLine >= line;
            });
    }

    private static Dictionary<string, string> BuildChangeMap(VersionedGraph graph, IReadOnlySet<string>? selectedChangeIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in graph.Changes)
        {
            if (selectedChangeIds is not null && !selectedChangeIds.Contains(change.Id)) continue;
            var id = graph.Versions.FirstOrDefault(version => version.Id == change.AfterSymbolVersionId)?.IdentityId
                     ?? graph.Versions.FirstOrDefault(version => version.Id == change.BeforeSymbolVersionId)?.IdentityId;
            if (id is not null) result[id] = change.Type switch { SymbolChangeKind.AddSymbol => "added", SymbolChangeKind.RemoveSymbol => "deleted", _ => "modified" };
        }
        return result;
    }

    internal static IReadOnlyList<SymbolVersion> CurrentVersions(VersionedGraph graph, GitComparison comparison) =>
        CurrentVersions(graph, comparison, DiagramRevisionSide.Combined);

    internal static IReadOnlyList<SymbolVersion> CurrentVersions(
        VersionedGraph graph, GitComparison comparison, DiagramRevisionSide revisionSide) => graph.Versions
        .Where(version => revisionSide == DiagramRevisionSide.Combined ||
                          version.RevisionSha == RevisionSha(comparison, revisionSide))
        .GroupBy(static version => version.IdentityId, StringComparer.Ordinal)
        .Select(group => group
            .OrderByDescending(version => version.RevisionSha == comparison.TargetSha)
            .ThenByDescending(static version => version.EndLine - version.StartLine)
            .ThenBy(static version => version.FilePath, StringComparer.Ordinal)
            .ThenBy(static version => version.Id, StringComparer.Ordinal)
            .First())
        .OrderBy(static version => version.IdentityId, StringComparer.Ordinal)
        .ToArray();

    private static Dictionary<string, string> BuildOwners(IReadOnlyList<SymbolVersion> versions, IReadOnlyList<SymbolVersion> typeVersions)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var owner = typeVersions.Where(type => IsOwnedBy(version.QualifiedName, type.QualifiedName)).OrderByDescending(static type => type.QualifiedName.Length).FirstOrDefault();
            result[version.IdentityId] = owner?.IdentityId ?? version.IdentityId;
        }
        return result;
    }

    private static string LabelForIdentity(VersionedGraph graph, GitComparison comparison, string identityId) =>
        CurrentVersions(graph, comparison).FirstOrDefault(version => version.IdentityId == identityId)?.QualifiedName ?? identityId;
    private static string OwnerLabel(IReadOnlyList<SymbolVersion> versions, string identityId) =>
        versions.FirstOrDefault(version => version.IdentityId == identityId)?.QualifiedName ?? "전역 함수";
    private static string ShapeForControl(string kind) => kind switch
    {
        "entry" or "exit" => "terminal",
        "condition" or "loop" => "decision",
        "call" => "call",
        "return" => "return",
        _ => "operation"
    };
    private static bool IsOwnedBy(string member, string type) => member.StartsWith(type + ".", StringComparison.Ordinal) || member.StartsWith(type + "::", StringComparison.Ordinal);
    private static string LastQualifiedPart(string value) => value.Split(["::", "."], StringSplitOptions.RemoveEmptyEntries).Last();
    private static bool IsType(VersionedGraph graph, string identityId) => graph.Identities.FirstOrDefault(identity => identity.Id == identityId)?.Kind is { } kind &&
        (kind.Contains("type", StringComparison.OrdinalIgnoreCase) || kind.Contains("class", StringComparison.OrdinalIgnoreCase) || kind.Contains("interface", StringComparison.OrdinalIgnoreCase) || kind.Contains("struct", StringComparison.OrdinalIgnoreCase));
    private static int ResolveMaximum(string? detail, int fallback, bool nodes) => detail?.ToLowerInvariant() switch
    { "compact" => nodes ? Math.Min(fallback, 20) : Math.Min(fallback, 30), "detailed" => nodes ? Math.Max(fallback, 60) : Math.Max(fallback, 100), _ => fallback };
    private static string NormalizeDirection(string? direction) => direction?.Equals("TB", StringComparison.OrdinalIgnoreCase) == true ? "TB" : "LR";
    private static string NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    { "flow" or "dependency" or "component" => "flowchart", "classdiagram" => "class", "coderelation" or "er" => "code-relation", _ => type.Trim().ToLowerInvariant() };
}
