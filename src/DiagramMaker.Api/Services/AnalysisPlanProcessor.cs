using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed class AnalysisPlanProcessor(
    IAppStore store,
    IGitWorkerClient git,
    SourceGraphAnalyzer analyzer,
    IInternalLlmClient llm,
    ILogger<AnalysisPlanProcessor> logger)
{
    public async Task ProcessAsync(AnalysisPlan leasedPlan, CancellationToken cancellationToken)
    {
        var current = leasedPlan;
        try
        {
            var repository = await store.GetRepositoryAsync(current.Request.RepositoryId, cancellationToken)
                             ?? throw new InvalidOperationException("Repository is no longer registered.");
            var indexVersion = BuildIndexVersion(repository);
            var target = await git.GetCommitAsync(repository, current.Request.TargetRevision, cancellationToken);
            var baseRevision = string.IsNullOrWhiteSpace(current.Request.BaseRevision)
                ? target.ParentShas.FirstOrDefault()
                : current.Request.BaseRevision;
            if (string.IsNullOrWhiteSpace(baseRevision))
                throw new GitWorkerException("GIT_REVISION_NOT_FOUND", "The target commit does not have a parent commit.");
            var baseCommit = await git.GetCommitAsync(repository, baseRevision, cancellationToken);

            current = await UpdateAsync(current with
            {
                State = AnalysisPlanState.Indexing,
                BaseSha = baseCommit.Sha,
                TargetSha = target.Sha,
                Progress = 15,
                StageMessage = "관련 Visual Studio C++ 프로젝트를 인덱싱하고 있습니다"
            }, cancellationToken);

            GitComparison comparison;
            VersionedGraph graph;
            var warnings = new List<string>();
            AnalysisExclusionSummary? exclusions = null;
            var cached = (await store.ListAnalysisPlansAsync(current.OwnerUserId, 50, cancellationToken))
                .FirstOrDefault(plan => plan.Id != current.Id &&
                                        plan.State == AnalysisPlanState.Ready &&
                                        plan.Request.RepositoryId == repository.Id &&
                                        plan.BaseSha == baseCommit.Sha &&
                                        plan.TargetSha == target.Sha &&
                                        plan.IndexVersion == indexVersion &&
                                        plan.Comparison is not null && plan.Graph is not null &&
                                        plan.ExpiresAt > DateTimeOffset.UtcNow);
            if (cached is not null)
            {
                comparison = cached.Comparison!;
                graph = cached.Graph!;
                exclusions = cached.Exclusions;
                warnings.Add("동일한 커밋 범위의 30일 인덱스 캐시를 재사용했습니다.");
                if (current.Request.UseLlmGrouping && llm.IsEnabled)
                {
                    comparison = await git.CompareAsync(repository, new AnalyzeRequest(
                        repository.Id, baseCommit.Sha, target.Sha, IncludeLlmSummary: false), cancellationToken);
                }
            }
            else
            {
                var prepared = await git.PrepareAsync(repository, baseCommit.Sha, target.Sha, cancellationToken);
                comparison = prepared.Comparison;
                graph = analyzer.Analyze(repository.Id, comparison, prepared.CppIndex);
                warnings.AddRange(BuildIndexWarnings(prepared.CppIndex));
                exclusions = BuildExclusions(prepared.CppIndex);
            }

            var candidates = BuildCandidates(graph);
            var groups = BuildStaticGroups(candidates, graph);
            if (current.Request.UseLlmGrouping && llm.IsEnabled && candidates.Count > 0)
            {
                current = await UpdateAsync(current with
                {
                    State = AnalysisPlanState.Grouping,
                    Progress = 75,
                    StageMessage = "근거 범위 안에서 변경점을 그룹화하고 있습니다"
                }, cancellationToken);
                try
                {
                    var suggestedGroups = await llm.RegroupChangesAsync(
                        candidates, groups, graph, comparison.Files,
                        current.Request.EnableThinking, cancellationToken);
                    if (suggestedGroups is null)
                    {
                        warnings.Add("변경 범위가 LLM 입력 한도를 넘어 정적 호출 관계 기반 그룹을 표시합니다.");
                    }
                    else
                    {
                        groups = suggestedGroups;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "LLM grouping failed for analysis plan {PlanId}; static groups remain available.", current.Id);
                    warnings.Add("LLM 그룹 제안에 실패하여 정적 호출 관계 기반 그룹을 표시합니다.");
                }
            }
            else if (current.Request.UseLlmGrouping)
            {
                warnings.Add("내부 LLM이 비활성화되어 정적 호출 관계 기반 그룹을 표시합니다.");
            }

            var safeComparison = comparison with
            {
                Files = comparison.Files.Select(static file => file with
                {
                    BeforeContent = null,
                    AfterContent = null
                }).ToArray(),
                ContextFiles = []
            };
            var selections = groups.Select(group => new AnalysisGroupSelection(
                group.Id,
                group.Title,
                group.ChangeIds,
                group.SuggestedDiagramType,
                "balanced")).ToArray();
            await UpdateAsync(current with
            {
                State = AnalysisPlanState.Ready,
                BaseSha = comparison.BaseSha,
                TargetSha = comparison.TargetSha,
                Progress = 100,
                StageMessage = "변경점을 선택하고 그룹화한 뒤 다이어그램을 생성하세요",
                Comparison = safeComparison,
                Graph = graph,
                Candidates = candidates,
                SuggestedGroups = groups,
                Selections = selections,
                Warnings = warnings,
                Revision = current.Revision + 1,
                LeaseUntil = null,
                IndexVersion = indexVersion,
                Exclusions = exclusions
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis plan {PlanId} failed at stage {State}.", current.Id, current.State);
            await store.SaveAnalysisPlanAsync(current with
            {
                State = AnalysisPlanState.Failed,
                Progress = 100,
                StageMessage = "사전 분석 실패",
                ErrorCode = exception is GitWorkerException gitException ? gitException.ErrorCode : "ANALYSIS_PLAN_FAILED",
                ErrorMessage = exception is GitWorkerException safeGitException
                    ? safeGitException.UserMessage
                    : "사전 분석에 실패했습니다. 계획 ID로 내부 서버 로그를 확인하세요.",
                LeaseUntil = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }

    private async Task<AnalysisPlan> UpdateAsync(AnalysisPlan plan, CancellationToken cancellationToken)
    {
        var updated = plan with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAnalysisPlanAsync(updated, cancellationToken);
        return updated;
    }

    internal static IReadOnlyList<ChangeCandidate> BuildCandidates(VersionedGraph graph)
    {
        return graph.Changes.Select(change =>
        {
            var version = graph.Versions.FirstOrDefault(item => item.Id == change.AfterSymbolVersionId)
                          ?? graph.Versions.FirstOrDefault(item => item.Id == change.BeforeSymbolVersionId);
            if (version is null) return null;
            var identity = graph.Identities.FirstOrDefault(item => item.Id == version.IdentityId);
            if (identity is null) return null;
            var callers = graph.Edges.Count(edge => edge.Type == "calls" && edge.ToIdentityId == identity.Id);
            var callees = graph.Edges.Count(edge => edge.Type == "calls" && edge.FromIdentityId == identity.Id);
            return new ChangeCandidate(
                change.Id, identity.Id, version.QualifiedName, identity.Kind, change.Type,
                version.FilePath, version.StartLine, version.EndLine, version.Signature,
                change.ContinuityConfidence, callers, callees, change.EvidenceIds);
        }).Where(static candidate => candidate is not null).Cast<ChangeCandidate>()
            .OrderBy(static candidate => candidate.FilePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.StartLine)
            .ToArray();
    }

    internal static IReadOnlyList<AnalysisGroupDraft> BuildStaticGroups(
        IReadOnlyList<ChangeCandidate> candidates,
        VersionedGraph graph)
    {
        if (candidates.Count == 0) return [];
        var parent = candidates.ToDictionary(static candidate => candidate.Id, static candidate => candidate.Id, StringComparer.Ordinal);
        var candidatesByIdentity = candidates
            .GroupBy(static candidate => candidate.IdentityId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        string Find(string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot) parent[rightRoot] = leftRoot;
        }

        foreach (var identityCandidates in candidatesByIdentity.Values)
        {
            for (var index = 1; index < identityCandidates.Length; index++)
                Union(identityCandidates[0].Id, identityCandidates[index].Id);
        }

        foreach (var edge in graph.Edges.Where(static edge => edge.Type is "calls" or "inherits"))
        {
            if (candidatesByIdentity.TryGetValue(edge.FromIdentityId, out var left) &&
                candidatesByIdentity.TryGetValue(edge.ToIdentityId, out var right)) Union(left[0].Id, right[0].Id);
        }
        foreach (var fileGroup in candidates.GroupBy(static candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var values = fileGroup.ToArray();
            for (var index = 1; index < values.Length; index++) Union(values[0].Id, values[index].Id);
        }

        var result = new List<AnalysisGroupDraft>();
        foreach (var connected in candidates.GroupBy(candidate => Find(candidate.Id)).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            foreach (var chunk in connected.Chunk(20))
            {
                var ids = chunk.Select(static candidate => candidate.Id).ToArray();
                var titleSource = CommonOwner(chunk) ?? Path.GetFileName(chunk[0].FilePath);
                var suggestedType = chunk.Any(static candidate => candidate.Kind is "class" or "type" or "struct")
                    ? "class"
                    : HasInternalCalls(chunk, graph) ? "sequence" : "flowchart";
                result.Add(new AnalysisGroupDraft(
                    StableIds.Create("static-group", string.Join('|', ids.Order(StringComparer.Ordinal))),
                    $"{titleSource} 변경",
                    $"{chunk.Length}개 변경 심볼과 직접 호출 관계를 기준으로 묶은 그룹입니다.",
                    ids,
                    "static",
                    Confidence.Exact,
                    suggestedType));
            }
        }
        if (result.Count <= 50)
        {
            return result;
        }

        var compacted = new List<AnalysisGroupDraft>();
        var chunkSize = (int)Math.Ceiling(candidates.Count / 50d);
        foreach (var chunk in candidates.Chunk(chunkSize))
        {
            var ids = chunk.Select(static candidate => candidate.Id).ToArray();
            var titleSource = CommonOwner(chunk) ?? Path.GetFileName(chunk[0].FilePath);
            var suggestedType = chunk.Any(static candidate => candidate.Kind is "class" or "type" or "struct")
                ? "class"
                : HasInternalCalls(chunk, graph) ? "sequence" : "flowchart";
            compacted.Add(new AnalysisGroupDraft(
                StableIds.Create("static-group", string.Join('|', ids.Order(StringComparer.Ordinal))),
                $"{titleSource} 변경",
                $"{chunk.Length}개 변경 사항을 검토 가능한 크기로 묶은 그룹입니다.",
                ids,
                "static-compacted",
                Confidence.Exact,
                suggestedType));
        }

        return compacted;
    }

    private static string? CommonOwner(IReadOnlyList<ChangeCandidate> candidates)
    {
        var owners = candidates.Select(static candidate =>
        {
            var separator = candidate.QualifiedName.Contains("::", StringComparison.Ordinal) ? "::" : ".";
            var index = candidate.QualifiedName.LastIndexOf(separator, StringComparison.Ordinal);
            return index > 0 ? candidate.QualifiedName[..index] : null;
        }).Distinct(StringComparer.Ordinal).ToArray();
        return owners.Length == 1 ? owners[0] : null;
    }

    private static bool HasInternalCalls(IReadOnlyList<ChangeCandidate> candidates, VersionedGraph graph)
    {
        var ids = candidates.Select(static candidate => candidate.IdentityId).ToHashSet(StringComparer.Ordinal);
        return graph.Edges.Any(edge => edge.Type == "calls" && ids.Contains(edge.FromIdentityId) && ids.Contains(edge.ToIdentityId));
    }

    private static IEnumerable<string> BuildIndexWarnings(CppSourceIndex index)
    {
        if (index.Truncated)
            yield return $"C++ 인덱스가 안전 한도에서 잘렸습니다. {index.IndexedFileCount:N0}개 파일만 분석했습니다.";
        if (index.ExcludedCallCount > 0)
            yield return $"해석이 모호한 C++ 호출 {index.ExcludedCallCount:N0}개는 다이어그램 관계에서 제외했습니다.";
        foreach (var diagnostic in index.Diagnostics.Take(20)) yield return diagnostic;
    }

    private static AnalysisExclusionSummary? BuildExclusions(CppSourceIndex index)
    {
        if (index.ExcludedCallCount <= 0) return null;
        var calls = index.ExcludedCalls ?? [];
        return new AnalysisExclusionSummary(
            index.ExcludedCallCount,
            calls.Select(static call => call.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            index.ExcludedCallsTruncated,
            calls);
    }

    private static string BuildIndexVersion(RepositoryDefinition repository)
    {
        var rules = repository.AnalysisRules?.IndirectCalls ?? [];
        var fingerprint = string.Join('|', rules.OrderBy(static rule => rule.Id, StringComparer.Ordinal).Select(rule =>
            $"{rule.Id}:{rule.Enabled}:{rule.ApiName}:{rule.TargetTypeArgumentIndex}:{rule.TargetMethodArgumentIndex}:" +
            string.Join(',', rule.Aliases.OrderBy(static alias => alias.Expression, StringComparer.Ordinal)
                .Select(static alias => $"{alias.Expression}={alias.TargetType}"))));
        return $"{SourceGraphAnalyzer.IndexVersion}:{StableIds.Create(fingerprint)}";
    }
}
