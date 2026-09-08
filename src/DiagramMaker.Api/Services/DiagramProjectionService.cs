using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed record DiagramProjectionResult(IReadOnlyList<DiagramArtifact> Artifacts, IReadOnlyList<DiagramAvailability> Availability);

public sealed class DiagramProjectionService
{
    private const int DisplayNodeLimit = 80;
    private const int DisplayEdgeLimit = 120;
    private static readonly string[] SupportedTypes = ["flowchart", "class", "sequence", "code-relation", "state"];

    public DiagramProjectionResult Build(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlyList<string>? requestedTypes,
        int callerDepth, int calleeDepth, bool contextFilesTruncated,
        IReadOnlySet<string>? selectedChangeIds = null, DiagramPreset? preset = null, DiagramStyleOverrides? overrides = null,
        bool focusOnChanges = false, bool preserveDetails = false)
    {
        var types = (requestedTypes is null || requestedTypes.Count == 0 ? ["flowchart"] : requestedTypes)
            .Select(NormalizeType).Distinct(StringComparer.Ordinal).ToArray();
        var changes = BuildChangeMap(graph, selectedChangeIds);
        callerDepth = Math.Clamp(overrides?.CallerDepth ?? preset?.CallerDepth ?? callerDepth, 0, 3);
        calleeDepth = Math.Clamp(overrides?.CalleeDepth ?? preset?.CalleeDepth ?? calleeDepth, 0, 3);
        var relationDepth = Math.Clamp(overrides?.RelationDepth ?? preset?.RelationDepth ?? 1, 0, 3);
        var maximumNodes = ResolveMaximum(overrides?.DetailLevel, preset?.MaximumNodes ?? DisplayNodeLimit, true);
        var maximumEdges = ResolveMaximum(overrides?.DetailLevel, preset?.MaximumEdges ?? DisplayEdgeLimit, false);
        // Limits belong to individual document pages, never to the source projection.
        if (preserveDetails) { maximumNodes = int.MaxValue; maximumEdges = int.MaxValue; }
        var direction = NormalizeDirection(overrides?.Direction ?? preset?.Direction);
        var selected = SelectImpact(graph, comparison, changes.Keys, callerDepth, calleeDepth);
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
                "class" => BuildClass(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, relationDepth, focusOnChanges),
                "sequence" => BuildSequence(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges),
                "code-relation" => BuildCodeRelation(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, relationDepth, focusOnChanges),
                _ => BuildFlow(repositoryName, graph, comparison, selected, changes, contextFilesTruncated, maximumNodes, maximumEdges, direction, focusOnChanges)
            };
            if (ir.Nodes.Count == 0)
            {
                availability.Add(new DiagramAvailability(type, false, type == "flowchart"
                    ? "지원하지 않는 제어 구문이 있거나 제어 흐름 근거가 없어 정확한 흐름도를 제공할 수 없습니다."
                    : "선택한 변경과 연결되는 호출·타입 관계 근거가 없어 이 유형을 생성할 수 없습니다."));
                continue;
            }
            var symbolNames = graph.Versions.Select(version => version.QualifiedName).ToHashSet(StringComparer.Ordinal);
            var labels = DiagramCodeLabels.ShortNames(ir.Nodes.Select(node => node.Label).Where(symbolNames.Contains));
            var groups = DiagramCodeLabels.ShortNames(ir.Nodes.Select(node => node.Group).OfType<string>().Select(value => value.Replace(" · 변경 전 근거", "", StringComparison.Ordinal)));
            ir = ir with { Nodes = ir.Nodes.Select(node => node with
            {
                Label = labels.GetValueOrDefault(node.Label, node.Label),
                QualifiedName = symbolNames.Contains(node.Label) ? node.Label : node.QualifiedName,
                Group = node.Group is null ? null : groups.GetValueOrDefault(node.Group.Replace(" · 변경 전 근거", "", StringComparison.Ordinal), node.Group) +
                    (node.Group.EndsWith(" · 변경 전 근거", StringComparison.Ordinal) ? " · 변경 전 근거" : "")
            }).ToArray() };
            artifacts.Add(new DiagramArtifact(Guid.NewGuid(), type, 1, ir, string.Empty, DateTimeOffset.UtcNow));
            availability.Add(new DiagramAvailability(type, true, null));
        }
        return new DiagramProjectionResult(artifacts, availability);
    }

    public static bool IsSupported(string type) => SupportedTypes.Contains(NormalizeType(type), StringComparer.Ordinal);

    private static DiagramIr BuildFlow(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges)
    {
        var selectedFlows = (graph.ControlFlows ?? [])
            .Where(flow => changes.ContainsKey(flow.IdentityId))
            .GroupBy(static flow => $"{flow.IdentityId}\0{flow.RevisionSha}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(flow => flow.RevisionSha == comparison.TargetSha)
                .ThenByDescending(static flow => flow.Nodes.Count)
                .ThenBy(static flow => flow.FilePath, StringComparer.Ordinal)
                .First())
            .ToArray();
        if (selectedFlows.Length == 0)
        {
            return CreateIr("flowchart", repositoryName, comparison, [], [], truncated, maxNodes, maxEdges, direction,
                "제어 흐름 근거를 확보하지 못했습니다. 호출 그래프를 흐름도로 대신 표시하지 않습니다.");
        }

        var versions = CurrentVersions(graph, comparison).ToDictionary(static version => version.IdentityId, StringComparer.Ordinal);
        var nodes = new List<DiagramNode>();
        var edges = new List<DiagramEdge>();
        foreach (var flow in selectedFlows.OrderBy(static item => item.IdentityId, StringComparer.Ordinal))
        {
            var group = (versions.GetValueOrDefault(flow.IdentityId)?.QualifiedName ?? flow.IdentityId) +
                (flow.RevisionSha == comparison.BaseSha ? " · 변경 전 근거" : "");
            var isBaseGhostFlow = flow.RevisionSha == comparison.BaseSha &&
                                  selectedFlows.Any(candidate => candidate.IdentityId == flow.IdentityId && candidate.RevisionSha == comparison.TargetSha);
            if (isBaseGhostFlow && !flow.Nodes.Any(node => MarkerForControl(graph, comparison, flow, node, changes)?.Kind == DiagramChangeKind.Deleted)) continue;
            var visibleFlowNodes = flow.Nodes.ToArray();
            var visibleIds = visibleFlowNodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
            nodes.AddRange(visibleFlowNodes.Select(node => new DiagramNode(
                node.Id, FallbackControlLabel(node), node.Kind, group, MarkerForControl(graph, comparison, flow, node, changes)?.Kind.ToString().ToLowerInvariant() ?? "unchanged",
                Confidence.Exact, node.EvidenceIds, ShapeForControl(node.Kind),
                ControlDetails(node, graph, comparison),
                MarkerForControl(graph, comparison, flow, node, changes), [node.Id], Context: node.Context)));
            edges.AddRange(flow.Edges.Where(edge => visibleIds.Contains(edge.SourceId) && visibleIds.Contains(edge.TargetId)).Select((edge, index) => new DiagramEdge(
                StableIds.Create(flow.IdentityId, edge.SourceId, edge.TargetId, edge.Type, index), edge.SourceId, edge.TargetId,
                edge.Type, edge.Label, "unchanged", Confidence.Exact,
                flow.Nodes.FirstOrDefault(node => node.Id == edge.SourceId)?.EvidenceIds ?? [],
                ChangeMarker: nodes.LastOrDefault(node => node.Id == edge.SourceId)?.ChangeMarker)));
        }
        var limitedNodes = nodes.DistinctBy(static node => node.Id).Take(maxNodes).ToArray();
        var limitedEdges = KeepEdgesBetweenNodes(edges.DistinctBy(static edge => edge.Id), limitedNodes, maxEdges);
        // Focus changes by selecting methods, never by deleting intermediate CFG paths.
        return CreateIr("flowchart", repositoryName, comparison, limitedNodes, limitedEdges, truncated, maxNodes, maxEdges, direction,
            "선택한 변경 메서드의 조건, 반복, 호출 및 종료 경로입니다. 원문은 근거에서 확인하세요.");
    }

    private static DiagramIr BuildSequence(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction,
        bool focusOnChanges)
    {
        var callable = selected.Where(id => graph.Identities.FirstOrDefault(identity => identity.Id == id)?.Kind is "method" or "constructor" or "function")
            .ToHashSet(StringComparer.Ordinal);
        if (callable.Count == 0) callable = selected.ToHashSet(StringComparer.Ordinal);
        var versions = CurrentVersions(graph, comparison);
        var owners = BuildOwners(versions, versions.Where(version => IsType(graph, version.IdentityId)).ToArray());
        var names = DiagramCodeLabels.ShortNames(versions.Select(version => version.QualifiedName));
        var calls = CreateEdges(graph, comparison, callable, changes, true, int.MaxValue)
            .Where(edge => edge.Type == "calls" && edge.Status != "deleted").ToArray();
        var byCaller = calls.GroupBy(edge => edge.SourceId).ToDictionary(group => group.Key,
            group => group.OrderBy(edge => edge.SequenceIndex ?? int.MaxValue).ToArray());
        var roots = byCaller.Keys.Where(id => calls.All(edge => edge.TargetId != id)).ToArray();
        if (roots.Length == 0) roots = byCaller.Keys.Where(changes.ContainsKey).ToArray();
        var nodes = new Dictionary<string, DiagramNode>();
        var events = new List<DiagramEdge>();
        var scenarios = new List<SequenceBlock>();
        var expansionLimit = Math.Min(maxEdges, 20_000);
        var expansionLimited = false;
        var visitedSources = new HashSet<string>();
        foreach (var root in roots.Concat(byCaller.Keys).Distinct())
        {
            if (visitedSources.Contains(root)) continue;
            var reachable = new HashSet<string> { root };
            for (var count = 0; count < callable.Count; count++)
            {
                var oldCount = reachable.Count;
                reachable.UnionWith(calls.Where(edge => reachable.Contains(edge.SourceId)).Select(edge => edge.TargetId));
                if (oldCount == reachable.Count) break;
            }
            if (!reachable.Any(changes.ContainsKey)) continue;
            var children = Expand(root, [], 0, root);
            if (children.Count > 0) scenarios.Add(new SequenceBlock(StableIds.Create("scenario", root), "scenario",
                $"호출 시나리오: {ShortName(root)}", children));
        }
        var result = CreateIr("sequence", repositoryName, comparison, nodes.Values.ToArray(), events, truncated || expansionLimited,
            maxNodes, maxEdges, direction, "소스에 근거한 호출 시나리오이며 실제 실행 기록이 아닙니다. 조건·반복은 코드상 가능한 경로를 표시합니다.")
            with { SequenceBlocks = scenarios };
        return expansionLimited ? result with { Notes = result.Notes.Append("분석 한도: 시나리오 확장 한도(16수준 또는 20,000회)에 도달해 일부 경로를 제공하지 않습니다.").ToArray() } : result;

        string Participant(string identityId)
        {
            var version = versions.First(version => version.IdentityId == identityId);
            var ownerId = owners.GetValueOrDefault(identityId, identityId);
            var owner = versions.FirstOrDefault(item => item.IdentityId == ownerId && IsType(graph, ownerId));
            var id = owner?.IdentityId ?? StableIds.Create("module", version.FilePath);
            if (!nodes.ContainsKey(id)) nodes[id] = new DiagramNode(id, owner is null ? version.FilePath : names[owner.QualifiedName],
                "participant", null, "unchanged", Confidence.Exact, [], Details: [owner?.QualifiedName ?? version.FilePath], SourceFactIds: [],
                QualifiedName: owner?.QualifiedName);
            var facts = graph.Evidence.Where(item => item.FilePath == version.FilePath && item.RevisionSha == version.RevisionSha &&
                item.StartLine >= version.StartLine && item.EndLine <= version.EndLine).Select(item => item.Id);
            nodes[id] = nodes[id] with { EvidenceIds = nodes[id].EvidenceIds.Concat(facts).Distinct().ToArray(),
                SourceFactIds = (nodes[id].SourceFactIds ?? []).Append(version.Id).Distinct().ToArray(),
                ChangeMarker = changes.ContainsKey(identityId) ? MarkerForSymbol(version, changes[identityId], facts.ToArray()) : nodes[id].ChangeMarker };
            return id;
        }
        IReadOnlyList<SequenceBlock> Expand(string caller, HashSet<string> stack, int depth, string path)
        {
            if (stack.Contains(caller) || !byCaller.TryGetValue(caller, out var outgoing)) return [];
            if (depth >= 16) { expansionLimited = true; return []; }
            visitedSources.Add(caller);
            var nextStack = stack.Append(caller).ToHashSet();
            var local = new List<DiagramEdge>();
            var childrenByEvent = new Dictionary<string, IReadOnlyList<SequenceBlock>>();
            foreach (var call in outgoing)
            {
                if (events.Count >= expansionLimit) { expansionLimited = true; break; }
                var id = StableIds.Create(path, call.Id);
                var projected = call with { Id = id, SourceFactIds = [call.Id], SourceId = Participant(call.SourceId), TargetId = Participant(call.TargetId),
                    Label = call.Context is null ? ShortName(call.TargetId) : DiagramCodeLabels.Action(call.Context) };
                events.Add(projected);
                local.Add(projected);
                childrenByEvent[id] = Expand(call.TargetId, nextStack, depth + 1, id);
            }
            return Attach(SequenceStructure.FromEdges(local));
            IReadOnlyList<SequenceBlock> Attach(IReadOnlyList<SequenceBlock> blocks) => blocks.Select(block => block.Kind == "message"
                ? new SequenceBlock(block.Id + "_step", "sequence", "", new[] { block }.Concat(childrenByEvent[block.Id]).ToArray())
                : block with { Children = Attach(block.Children) }).ToArray();
        }
        string ShortName(string identity) => versions.FirstOrDefault(version => version.IdentityId == identity) is { } version
            ? names[version.QualifiedName] : identity;
    }

    private static DiagramIr BuildClass(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction, int relationDepth,
        bool focusOnChanges)
    {
        var versions = CurrentVersions(graph, comparison);
        var typeVersions = versions.Where(version => IsType(graph, version.IdentityId)).ToArray();
        var owners = BuildOwners(versions, typeVersions);
        var selectedOwners = selected.Select(id => owners.GetValueOrDefault(id, id)).Where(id => typeVersions.Any(type => type.IdentityId == id)).ToHashSet(StringComparer.Ordinal);
        var ownerEdges = EdgesForRevision(graph, comparison)
            .Where(static edge => edge.Type is "calls" or "inherits" or "implements" or "association" or "depends")
            .Select(edge => new DiagramEdge(edge.Id,
                owners.GetValueOrDefault(edge.FromIdentityId, edge.FromIdentityId),
                owners.GetValueOrDefault(edge.ToIdentityId, edge.ToIdentityId), edge.Type,
                edge.IsIndirect ? $"간접 API {edge.ViaApi}" : edge.Label, "unchanged", edge.Confidence,
                edge.EvidenceIds, edge.SequenceIndex, edge.IsIndirect, edge.ViaApi, edge.ControlPath,
                MarkerForGraphEdge(graph, comparison, edge)))
            .Where(edge => edge.SourceId != edge.TargetId && typeVersions.Any(type => type.IdentityId == edge.SourceId) && typeVersions.Any(type => type.IdentityId == edge.TargetId))
            .ToArray();
        selectedOwners = ExpandConnected(selectedOwners, ownerEdges, relationDepth);
        var nodes = CreateNodes(graph, comparison, selectedOwners, changes, owners, versions, maxNodes)
            .Select(node => node with
            {
                Details = graph.Versions.Where(version => version.IdentityId == node.Id &&
                    version.RevisionSha == typeVersions.First(item => item.IdentityId == node.Id).RevisionSha)
                    .SelectMany(version => (version.Members ?? []).Select(member => (Version: version, Member: member)))
                    .OrderByDescending(item => OverlapsChangedRange(comparison, item.Version.RevisionSha, item.Version.FilePath, item.Member.StartLine, item.Member.EndLine))
                    .ThenBy(item => AccessRank(item.Member.Accessibility)).ThenBy(item => item.Member.StartLine)
                    .Select(item => FormatClassMember(item.Member)).Distinct().ToArray(),
                SourceFactIds = (node.SourceFactIds ?? []).Concat(versions.Where(version =>
                    version.OwnerIdentityId == node.Id && changes.ContainsKey(version.IdentityId)).Select(version => version.Id)).Distinct().ToArray()
            }).ToArray();
        var edges = ownerEdges.Where(edge => selectedOwners.Contains(edge.SourceId) && selectedOwners.Contains(edge.TargetId))
            .Select(edge => edge with { Label = ClassRelationLabel(edge) })
            .ToArray();
        edges = CollapseLogicalEdges(edges, maxEdges);
        edges = KeepEdgesBetweenNodes(edges, nodes, maxEdges);
        if (focusOnChanges) (nodes, edges) = FocusDiagram(nodes, edges);
        return CreateIr("class", repositoryName, comparison, nodes, edges, truncated, maxNodes, maxEdges, direction,
            "변경 메서드를 소유 클래스에 축약하고 상속 및 호출 의존 방향을 표시합니다.");
    }

    private static DiagramIr BuildCodeRelation(
        string repositoryName, VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected,
        IReadOnlyDictionary<string, string> changes, bool truncated, int maxNodes, int maxEdges, string direction, int relationDepth,
        bool focusOnChanges)
    {
        var versions = CurrentVersions(graph, comparison);
        var owners = BuildOwners(versions, versions.Where(version => IsType(graph, version.IdentityId)).ToArray());
        var nodes = CreateNodes(graph, comparison, selected, changes, maximumNodes: maxNodes)
            .Select(node => node with
            {
                Label = LastQualifiedPart(node.Label),
                Group = OwnerLabel(versions, owners.GetValueOrDefault(node.Id, node.Id)),
                Shape = IsType(graph, node.Id) ? "type" : "method"
            }).ToArray();
        var edges = KeepEdgesBetweenNodes(CreateEdges(graph, comparison, selected, changes, false, maxEdges), nodes, maxEdges)
            .Where(static edge => edge.Type.Equals("calls", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge with { Label = edge.IsIndirect ? $"간접 API: {edge.ViaApi}" : edge.Label }).ToArray();
        edges = CollapseLogicalEdges(edges, maxEdges);
        var mappedNodes = nodes.ToList();
        var mappedEdges = edges.ToList();
        foreach (var node in nodes.Where(node => changes.ContainsKey(node.Id)))
        {
            var actionId = StableIds.Create("responsibility", node.Id);
            var changeId = StableIds.Create("change-intent", node.Id);
            var action = node with { Id = actionId, Label = $"{node.Label} 처리 변경", Kind = "responsibility", Shape = null };
            var intent = node with { Id = changeId, Label = node.Status switch
            { "added" => "새 처리 추가", "deleted" => "기존 처리 제거", _ => "기존 동작 수정" }, Kind = "change", Shape = null };
            mappedNodes.Add(intent);
            mappedNodes.Add(action);
            mappedEdges.Add(new DiagramEdge(StableIds.Create(changeId, actionId), changeId, actionId, "explains", "구현 책임", "unchanged", Confidence.Inferred, node.EvidenceIds));
            mappedEdges.Add(new DiagramEdge(StableIds.Create(actionId, node.Id), actionId, node.Id, "implements", "구현 위치", "unchanged", Confidence.Exact, node.EvidenceIds));
        }
        return CreateIr("code-relation", repositoryName, comparison, mappedNodes, mappedEdges, truncated, maxNodes, maxEdges, direction,
            "동작 변화 → 구현 책임 → 구현 위치를 표시합니다. 설명 관계(구현 책임·구현 위치)와 실제 호출을 구분합니다.");
    }

    private static DiagramIr CreateIr(
        string type, string repositoryName, GitComparison comparison, IReadOnlyList<DiagramNode> nodes,
        IReadOnlyList<DiagramEdge> edges, bool truncated, int maxNodes, int maxEdges, string direction, string description)
    {
        var notes = new List<string> { description, "변경 심볼은 색상으로 구분하며 관계 근거는 Evidence에서 확인할 수 있습니다." };
        if (truncated) notes.Add("참조 문맥이 제한되어 일부 관계가 누락될 수 있습니다.");
        if (nodes.Count >= maxNodes || edges.Count >= maxEdges) notes.Add($"표시는 최대 {maxNodes}개 노드와 {maxEdges}개 관계로 제한합니다.");
        var title = $"{repositoryName}: {comparison.BaseSha[..8]} → {comparison.TargetSha[..8]}";
        return new DiagramIr(type, title, nodes, edges,
            notes, [comparison.BaseSha, comparison.TargetSha], direction);
    }

    private static IReadOnlyList<DiagramNode> CreateNodes(
        VersionedGraph graph, GitComparison comparison, IReadOnlySet<string> selected, IReadOnlyDictionary<string, string> changes,
        IReadOnlyDictionary<string, string>? ownerByIdentity = null, IReadOnlyList<SymbolVersion>? versions = null,
        int maximumNodes = DisplayNodeLimit)
    {
        versions ??= CurrentVersions(graph, comparison);
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
                    MarkerForSymbol(version, nodeStatus, evidence), [version.Id]);
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
                SourceFactIds = values.SelectMany(edge => edge.SourceFactIds ?? [edge.Id]).Distinct().ToArray(),
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
        bool sequence, int maxEdges)
    {
        var target = EdgesForRevision(graph, comparison).Where(edge => selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(static edge => edge.Type is "calls" or "inherits")
            .OrderBy(edge => changes.ContainsKey(edge.FromIdentityId) || changes.ContainsKey(edge.ToIdentityId) ? 0 : 1)
            .ThenBy(static edge => edge.SequenceIndex ?? int.MaxValue).ThenBy(static edge => edge.FromIdentityId, StringComparer.Ordinal)
            .ThenBy(static edge => edge.ToIdentityId, StringComparer.Ordinal)
            .Select((edge, index) => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "unchanged",
                edge.Confidence, edge.EvidenceIds, sequence ? edge.SequenceIndex ?? index + 1 : edge.SequenceIndex,
                edge.IsIndirect, edge.ViaApi, edge.ControlPath, MarkerForGraphEdge(graph, comparison, edge), Context: edge.Context));
        var deleted = graph.Edges
            .Where(edge => edge.RevisionSha == comparison.BaseSha && selected.Contains(edge.FromIdentityId) && selected.Contains(edge.ToIdentityId))
            .Where(static edge => edge.Type is "calls" or "inherits")
            .Where(edge => MarkerForGraphEdge(graph, comparison, edge)?.Kind == DiagramChangeKind.Deleted)
            .Select(edge => new DiagramEdge(edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "deleted",
                edge.Confidence, edge.EvidenceIds, sequence ? edge.SequenceIndex : null, edge.IsIndirect, edge.ViaApi,
                edge.ControlPath, MarkerForGraphEdge(graph, comparison, edge), Context: edge.Context));
        return target.Concat(deleted).DistinctBy(static edge => edge.Id).Take(maxEdges).ToArray();
    }

    private static HashSet<string> SelectImpact(
        VersionedGraph graph, GitComparison comparison, IEnumerable<string> roots, int callerDepth, int calleeDepth)
    {
        var available = CurrentVersions(graph, comparison)
            .Select(static version => version.IdentityId).ToHashSet(StringComparer.Ordinal);
        var selected = roots.Where(available.Contains).ToHashSet(StringComparer.Ordinal);
        var traversalEdges = EdgesForRevision(graph, comparison)
            .Concat(graph.Edges.Where(edge => edge.RevisionSha == comparison.BaseSha &&
                MarkerForGraphEdge(graph, comparison, edge)?.Kind == DiagramChangeKind.Deleted))
            .Where(static edge => edge.Type == "calls")
            .DistinctBy(static edge => edge.Id)
            .ToArray();
        var originIds = selected.ToArray();
        Expand(callerDepth, incoming: true);
        Expand(calleeDepth, incoming: false);
        return selected;

        void Expand(int maximumDepth, bool incoming)
        {
            var visited = originIds.ToHashSet(StringComparer.Ordinal);
            var frontier = originIds;
            for (var depth = 0; depth < maximumDepth; depth++)
            {
                var current = frontier.ToHashSet(StringComparer.Ordinal);
                frontier = traversalEdges.Where(edge => current.Contains(incoming ? edge.ToIdentityId : edge.FromIdentityId))
                    .Select(edge => incoming ? edge.FromIdentityId : edge.ToIdentityId).Where(available.Contains).Where(visited.Add).ToArray();
                selected.UnionWith(frontier);
            }
        }
    }

    private static IEnumerable<GraphEdge> EdgesForRevision(
        VersionedGraph graph, GitComparison comparison)
    {
        return graph.Edges.Where(edge => edge.RevisionSha is null || edge.RevisionSha == comparison.TargetSha);
    }

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
        IReadOnlyDictionary<string, string> changes)
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
        VersionedGraph graph, GitComparison comparison, GraphEdge edge)
    {
        if (edge.RevisionSha is null || edge.FilePath is null || edge.StartLine is null || edge.EndLine is null ||
            !OverlapsChangedRange(comparison, edge.RevisionSha, edge.FilePath, edge.StartLine.Value, edge.EndLine.Value)) return null;
        if (edge.RevisionSha == comparison.BaseSha)
        {
            var baseRank = CompatibleEdges(graph, edge, comparison.BaseSha).FindIndex(candidate => candidate.Id == edge.Id);
            var targetCount = CompatibleEdges(graph, edge, comparison.TargetSha).Count;
            if (baseRank < 0) return null;
            var kind = baseRank >= targetCount ? DiagramChangeKind.Deleted : (DiagramChangeKind?)null;
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

    internal static IReadOnlyList<SymbolVersion> CurrentVersions(
        VersionedGraph graph, GitComparison comparison) => graph.Versions
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
            if (version.OwnerIdentityId is not null && typeVersions.Any(type => type.IdentityId == version.OwnerIdentityId))
            { result[version.IdentityId] = version.OwnerIdentityId; continue; }
            var owner = typeVersions.Where(type => IsOwnedBy(version.QualifiedName, type.QualifiedName)).OrderByDescending(static type => type.QualifiedName.Length).FirstOrDefault();
            result[version.IdentityId] = owner?.IdentityId ?? version.IdentityId;
        }
        return result;
    }

    private static string LabelForIdentity(VersionedGraph graph, GitComparison comparison, string identityId) =>
        CurrentVersions(graph, comparison).FirstOrDefault(version => version.IdentityId == identityId)?.QualifiedName ?? identityId;
    private static IReadOnlyList<string> ControlDetails(ControlFlowNode node, VersionedGraph graph, GitComparison comparison)
    {
        var details = new List<string> { node.Label };
        if (node.CallTargetIdentityId is not null) details.Add(LabelForIdentity(graph, comparison, node.CallTargetIdentityId));
        return details;
    }
    private static string FallbackControlLabel(ControlFlowNode node)
    {
        if (node.Kind == "entry") return "시작";
        if (node.Kind == "exit") return "종료";
        if (node.Kind == "return") return "결과 반환";
        if (node.Kind == "break") return "현재 분기 종료";
        if (node.Kind == "continue") return "다음 반복 진행";
        if (node.Kind == "case") return node.Label;
        if (node.Kind == "condition") return node.Label.TrimStart().StartsWith("switch", StringComparison.Ordinal)
            ? "값에 따른 case 분기"
            : "조건에 따른 분기";
        if (node.Kind == "loop") return "반복 조건 확인";
        if (node.Context is not null) return DiagramCodeLabels.Action(node.Context);
        if (node.Kind == "call")
        {
            var target = node.CallTargetIdentityId is null ? null : node.Label;
            var call = System.Text.RegularExpressions.Regex.Matches(target ?? node.Label, @"([A-Za-z_]\w*(?:(?:::|\.|->)[A-Za-z_]\w*)*)\s*\(")
                .Cast<System.Text.RegularExpressions.Match>().Select(static match => match.Groups[1].Value).FirstOrDefault();
            return string.IsNullOrWhiteSpace(call) ? "관련 기능 호출" : $"{LastQualifiedPart(call.Replace("->", "::", StringComparison.Ordinal))} 호출";
        }
        var pointer = System.Text.RegularExpressions.Regex.Match(node.Label, @"\*\s*(?<name>[A-Za-z_]\w*)\s*=");
        if (pointer.Success) return $"{HumanizeVariable(pointer.Groups["name"].Value)} 포인터 할당";
        var declaration = System.Text.RegularExpressions.Regex.Match(node.Label, @"\b(?<name>[A-Za-z_]\w*)\s*=");
        if (declaration.Success) return $"{HumanizeVariable(declaration.Groups["name"].Value)} 값 설정";
        return "데이터 처리";
    }
    private static string HumanizeVariable(string value) => value.Length > 1 && value[0] == 'p' && char.IsUpper(value[1]) ? value[1..] : value;
    private static HashSet<string> ExpandConnected(IReadOnlySet<string> roots, IReadOnlyList<DiagramEdge> edges, int depth)
    {
        var selected = roots.ToHashSet(StringComparer.Ordinal);
        var frontier = selected.ToHashSet(StringComparer.Ordinal);
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges.Where(edge => frontier.Contains(edge.SourceId) || frontier.Contains(edge.TargetId)))
            {
                if (selected.Add(edge.SourceId)) next.Add(edge.SourceId);
                if (selected.Add(edge.TargetId)) next.Add(edge.TargetId);
            }
            frontier = next;
        }
        return selected;
    }
    private static string FormatClassMember(ClassMemberFact member) =>
        $"{AccessMarker(member.Accessibility)}{member.Signature}{(member.IsStatic ? "$" : string.Empty)}";
    private static string AccessMarker(string accessibility) => accessibility switch
    {
        "public" => "+ ",
        "protected" or "protected internal" or "private protected" => "# ",
        "private" => "- ",
        _ => "~ "
    };
    private static int AccessRank(string accessibility) => accessibility switch
    {
        "public" => 0,
        "protected" or "protected internal" or "private protected" => 1,
        "internal" => 2,
        _ => 3
    };
    private static string ClassRelationLabel(DiagramEdge edge) => edge.Type switch
    {
        "association" => string.IsNullOrWhiteSpace(edge.Label) ? "보유" : $"보유: {edge.Label}",
        "depends" => string.IsNullOrWhiteSpace(edge.Label) ? "타입 의존" : $"타입 의존: {edge.Label}",
        "calls" => edge.IsIndirect ? $"간접 호출: {edge.ViaApi}" : "호출 의존",
        _ => string.Empty
    };
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
