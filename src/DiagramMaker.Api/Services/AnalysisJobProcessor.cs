using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed class AnalysisJobProcessor(
    IAppStore store,
    IGitWorkerClient git,
    SourceGraphAnalyzer analyzer,
    IInternalLlmClient llm,
    MermaidCompiler compiler,
    DiagramProjectionService projection,
    DiagramPresetCatalog presets,
    ILogger<AnalysisJobProcessor> logger)
{
    public async Task ProcessAsync(AnalysisJob leasedJob, CancellationToken cancellationToken)
    {
        var currentJob = leasedJob;
        try
        {
            var repository = await store.GetRepositoryAsync(currentJob.Request.RepositoryId, cancellationToken)
                             ?? throw new InvalidOperationException("Repository is no longer registered.");
            var (comparison, graph, plan) = await ResolveAnalysisInputAsync(currentJob, repository, cancellationToken);
            var sourceResult = await ResolveSourceResultAsync(currentJob, cancellationToken);
            currentJob = await UpdateAsync(currentJob with
            {
                State = AnalysisState.Graphing,
                BaseSha = comparison.BaseSha,
                TargetSha = comparison.TargetSha,
                Progress = 55,
                StageMessage = "Building versioned symbol graph"
            }, cancellationToken);

            var deterministic = BuildDeterministicNarrative(graph, comparison.Files);
            var narrative = sourceResult?.Narrative ?? deterministic;
            var warnings = deterministic.Warnings.ToList();
            var llmSucceeded = sourceResult is not null;
            if (sourceResult is null && currentJob.Request.IncludeLlmSummary && llm.IsEnabled)
            {
                currentJob = await UpdateAsync(currentJob with
                {
                    State = AnalysisState.Summarizing,
                    Progress = 70,
                    StageMessage = "Requesting evidence-bound internal LLM review"
                }, cancellationToken);
                try
                {
                    var generated = await llm.GenerateReviewAsync(
                        graph, comparison.Files, currentJob.Request.EnableThinking, cancellationToken);
                    if (generated is not null)
                    {
                        narrative = generated;
                        llmSucceeded = true;
                    }
                    else warnings.Add("내부 LLM이 유효한 근거 기반 결과를 반환하지 않았습니다.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Internal LLM review failed for analysis {AnalysisId}; deterministic results remain available.", currentJob.Id);
                    warnings.Add("내부 LLM 요약에 실패하여 정적 분석 요약을 표시합니다.");
                }
            }
            else if (sourceResult is null && currentJob.Request.IncludeLlmSummary)
            {
                warnings.Add("내부 LLM이 비활성화되어 정적 분석 요약을 표시합니다.");
            }

            currentJob = await UpdateAsync(currentJob with
            {
                State = AnalysisState.Rendering,
                Progress = 85,
                StageMessage = "Compiling safe Mermaid diagrams"
            }, cancellationToken);
            var renderResult = plan is not null && currentJob.Request.Groups is { Count: > 0 }
                ? RenderGroups(repository.Name, graph, comparison, currentJob.Request.Groups, warnings,
                    currentJob.Id, sourceResult, currentJob.Request.RequestedViewIds)
                : RenderLegacy(repository.Name, graph, comparison, currentJob.Request, warnings, currentJob.Id);
            if (renderResult.ExpectedCount > 0 && renderResult.Artifacts.Count == 0)
            {
                throw new DiagramGenerationException(
                    "DIAGRAM_NO_VALID_OUTPUT",
                    "선택한 형식에서 유효한 다이어그램을 생성하지 못했습니다. 표시 범위와 깊이를 줄여 다시 시도하세요.");
            }

            narrative = narrative with
            {
                Warnings = narrative.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToArray()
            };
            var safeFiles = comparison.Files.Select(static file => file with
            {
                BeforeContent = null,
                AfterContent = null
            }).ToArray();
            var result = new AnalysisResult(
                safeFiles, graph, narrative, renderResult.Artifacts,
                renderResult.Availability, renderResult.Groups);
            var degraded = renderResult.Artifacts.Count != renderResult.ExpectedCount;
            var finalState = currentJob.Request.IncludeLlmSummary && !llmSucceeded || degraded
                ? AnalysisState.Partial
                : AnalysisState.Completed;
            await UpdateAsync(currentJob with
            {
                State = finalState,
                Progress = 100,
                StageMessage = finalState == AnalysisState.Completed
                    ? "Analysis completed"
                    : "Analysis completed with partial results",
                Result = result,
                LeaseUntil = null
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis {AnalysisId} failed at stage {State}.", currentJob.Id, currentJob.State);
            await store.SaveAnalysisAsync(currentJob with
            {
                State = AnalysisState.Failed,
                Progress = 100,
                StageMessage = "Analysis failed",
                ErrorCode = MapErrorCode(exception),
                ErrorMessage = SafeMessage(exception),
                LeaseUntil = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }

    private async Task<(GitComparison Comparison, VersionedGraph Graph, AnalysisPlan? Plan)> ResolveAnalysisInputAsync(
        AnalysisJob job,
        RepositoryDefinition repository,
        CancellationToken cancellationToken)
    {
        if (job.Request.AnalysisPlanId is { } planId)
        {
            var plan = await store.GetAnalysisPlanAsync(planId, cancellationToken)
                       ?? throw new InvalidOperationException("The analysis plan no longer exists.");
            if (plan.State != AnalysisPlanState.Ready || plan.Comparison is null || plan.Graph is null)
                throw new InvalidOperationException("The analysis plan is not ready.");
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The analysis plan has expired.");
            var planComparison = plan.Comparison;
            if (job.Request.IncludeLlmSummary && job.Request.SourceAnalysisId is null)
            {
                planComparison = await git.CompareAsync(repository, job.Request with
                {
                    BaseRevision = plan.BaseSha!,
                    TargetRevision = plan.TargetSha!
                }, cancellationToken);
                if (planComparison.BaseSha != plan.BaseSha || planComparison.TargetSha != plan.TargetSha)
                    throw new InvalidOperationException("The immutable analysis plan revisions no longer match.");
            }
            return (planComparison, plan.Graph, plan);
        }

        var comparison = await git.CompareAsync(repository, job.Request, cancellationToken);
        await UpdateAsync(job with
        {
            State = AnalysisState.Indexing,
            BaseSha = comparison.BaseSha,
            TargetSha = comparison.TargetSha,
            Progress = 25,
            StageMessage = "Extracting changed symbols"
        }, cancellationToken);
        return (comparison, analyzer.Analyze(repository.Id, comparison), null);
    }

    private async Task<AnalysisResult?> ResolveSourceResultAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        if (job.Request.SourceAnalysisId is not { } sourceAnalysisId) return null;
        var source = await store.GetAnalysisAsync(sourceAnalysisId, cancellationToken)
                     ?? throw new InvalidOperationException("The source analysis no longer exists.");
        if (source.Result is null || source.State is not (AnalysisState.Completed or AnalysisState.Partial))
            throw new InvalidOperationException("The source analysis does not have a reusable result.");
        if (source.Request.AnalysisPlanId != job.Request.AnalysisPlanId)
            throw new InvalidOperationException("The source analysis belongs to a different analysis plan.");
        return source.Result;
    }

    private RenderResult RenderLegacy(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        AnalyzeRequest request,
        List<string> warnings,
        Guid analysisId)
    {
        var projected = projection.Build(
            repositoryName, graph, comparison, request.DiagramTypes,
            request.CallerDepth, request.CalleeDepth, comparison.ContextFilesTruncated);
        var artifacts = new List<DiagramArtifact>();
        var availability = projected.Availability.ToDictionary(static item => item.Type, StringComparer.Ordinal);
        foreach (var artifact in projected.Artifacts)
        {
            try
            {
                artifacts.Add(artifact with { MermaidDsl = compiler.Compile(artifact.Ir) });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Diagram type {DiagramType} was rejected for analysis {AnalysisId}.", artifact.Type, analysisId);
                availability[artifact.Type] = new DiagramAvailability(artifact.Type, false, "유효하지 않은 관계가 있어 이 형식만 제외했습니다.");
                warnings.Add($"{artifact.Type} 다이어그램을 생성하지 못해 다른 결과만 표시합니다.");
            }
        }
        return new RenderResult(artifacts, availability.Values.ToArray(), null, projected.Artifacts.Count);
    }

    private RenderResult RenderGroups(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlyList<AnalysisGroupSelection> groups,
        List<string> warnings,
        Guid analysisId,
        AnalysisResult? sourceResult,
        IReadOnlyList<string>? requestedViewIds)
    {
        var artifacts = new List<DiagramArtifact>();
        var availability = new List<DiagramAvailability>();
        var groupResults = new List<AnalysisDiagramGroupResult>();
        var requested = requestedViewIds?.ToHashSet(StringComparer.Ordinal);
        var expectedCount = 0;
        foreach (var group in groups)
        {
            var groupWarnings = new List<string>();
            var viewResults = new List<AnalysisDiagramViewResult>();
            var sourceGroup = sourceResult?.DiagramGroups?.FirstOrDefault(item => item.GroupId == group.Id);
            var sourceViews = EffectiveResultViews(sourceGroup)
                .GroupBy(static item => item.ViewId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderByDescending(static item => item.Diagram is not null || item.ComparisonBaseDiagram is not null)
                        .ThenByDescending(static item => item.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                        .First(),
                    StringComparer.Ordinal);
            foreach (var view in group.EffectiveViews())
            {
                expectedCount += view.CompareRevisions ? 2 : 1;
                sourceViews.TryGetValue(view.Id, out var sourceView);
                var shouldRender = sourceView is null || sourceView.Selection != view ||
                                   requested is null || requested.Contains(view.Id);
                if (!shouldRender && sourceView!.Diagram is not null)
                {
                    var reused = sourceView with { Reused = true };
                    viewResults.Add(reused);
                    artifacts.Add(reused.Diagram);
                    if (reused.ComparisonBaseDiagram is not null) artifacts.Add(reused.ComparisonBaseDiagram);
                    availability.Add(new DiagramAvailability(view.DiagramType, true, null));
                    continue;
                }

                try
                {
                    var preset = presets.Resolve(view.DiagramType, view.PresetId);
                    var targetSide = view.CompareRevisions ? DiagramRevisionSide.Target : DiagramRevisionSide.Combined;
                    var projected = projection.Build(
                        repositoryName, graph, comparison, [view.DiagramType],
                        preset.CallerDepth, preset.CalleeDepth, comparison.ContextFilesTruncated,
                        group.ChangeIds.ToHashSet(StringComparer.Ordinal), preset, view.Overrides,
                        view.FocusOnChanges, targetSide);
                    availability.AddRange(projected.Availability);
                    var targetArtifact = projected.Artifacts.FirstOrDefault() is { } artifact
                        ? artifact with { MermaidDsl = compiler.Compile(artifact.Ir) }
                        : null;
                    DiagramArtifact? baseArtifact = null;
                    string? baseMessage = null;
                    if (view.CompareRevisions)
                    {
                        var baseProjection = projection.Build(
                            repositoryName, graph, comparison, [view.DiagramType],
                            preset.CallerDepth, preset.CalleeDepth, comparison.ContextFilesTruncated,
                            group.ChangeIds.ToHashSet(StringComparer.Ordinal), preset, view.Overrides,
                            view.FocusOnChanges, DiagramRevisionSide.Base);
                        availability.AddRange(baseProjection.Availability);
                        baseArtifact = baseProjection.Artifacts.FirstOrDefault() is { } baseValue
                            ? baseValue with { MermaidDsl = compiler.Compile(baseValue.Ir) }
                            : null;
                        baseMessage = baseProjection.Availability.FirstOrDefault(static item => !item.Available)?.Reason;
                    }

                    if (targetArtifact is not null) artifacts.Add(targetArtifact);
                    if (baseArtifact is not null) artifacts.Add(baseArtifact);
                    if (!view.CompareRevisions && targetArtifact is not null)
                    {
                        viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, targetArtifact, [], "Completed",
                            ComparisonBaseDiagram: baseArtifact));
                        continue;
                    }
                    if (view.CompareRevisions && (targetArtifact is not null || baseArtifact is not null))
                    {
                        if (targetArtifact is null || baseArtifact is null) expectedCount--;
                        var sideNotice = targetArtifact is null
                            ? "Target revision에는 선택 변경 요소가 없습니다."
                            : baseArtifact is null ? "Base revision에는 선택 변경 요소가 없습니다." : null;
                        viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, targetArtifact,
                            sideNotice is null ? [] : [sideNotice], "Completed", ComparisonBaseDiagram: baseArtifact));
                        continue;
                    }

                    var targetMessage = projected.Availability.FirstOrDefault(static item => !item.Available)?.Reason;
                    var message = view.CompareRevisions
                        ? targetMessage ?? baseMessage ?? "두 revision 모두에서 선택 변경 요소를 찾을 수 없습니다."
                        : targetMessage ?? "선택한 변경점으로 다이어그램을 만들 수 없습니다.";
                    groupWarnings.Add(message);
                    viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, targetArtifact, [message],
                        targetArtifact is null ? "Failed" : "Partial", "DIAGRAM_NO_VALID_OUTPUT", message,
                        ComparisonBaseDiagram: baseArtifact));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Diagram view {ViewId} ({DiagramType}/{PresetId}) in group {GroupId} was rejected for analysis {AnalysisId}.",
                        view.Id, view.DiagramType, view.PresetId, group.Id, analysisId);
                    var errorCode = exception is DiagramValidationException ? "DIAGRAM_INVALID" : "DIAGRAM_RENDER_FAILED";
                    var message = exception is DiagramValidationException
                        ? "다이어그램의 노드 또는 관계가 유효하지 않아 이 보기만 제외했습니다."
                        : "다이어그램 생성 중 오류가 발생하여 이 보기만 제외했습니다.";
                    availability.Add(new DiagramAvailability(view.DiagramType, false, message));
                    groupWarnings.Add(message);
                    viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, null, [message], "Failed",
                        errorCode, message));
                }
            }
            var groupNarrative = BuildGroupNarrative(group, graph, groupWarnings);
            var compiled = viewResults.Select(static item => item.Diagram).FirstOrDefault(static item => item is not null);
            groupResults.Add(new AnalysisDiagramGroupResult(
                group.Id, group.Title, group.ChangeIds, compiled, groupNarrative, groupWarnings, viewResults));
            warnings.AddRange(groupWarnings.Select(warning => $"[{group.Title}] {warning}"));
        }
        return new RenderResult(artifacts, availability, groupResults, expectedCount);
    }

    private static IReadOnlyList<AnalysisDiagramViewResult> EffectiveResultViews(AnalysisDiagramGroupResult? group)
    {
        if (group is null) return [];
        if (group.Views is { Count: > 0 }) return group.Views
            .GroupBy(static view => view.ViewId, StringComparer.Ordinal)
            .Select(static views => views.OrderByDescending(static view => view.Diagram is not null || view.ComparisonBaseDiagram is not null)
                .ThenByDescending(static view => view.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                .First())
            .ToArray();
        var selection = new DiagramViewSelection($"{group.GroupId}-view", group.Diagram?.Type ?? "flowchart", "balanced");
        return [new AnalysisDiagramViewResult(selection.Id, selection, group.Diagram, group.Warnings,
            group.Diagram is null ? "Failed" : "Completed")];
    }

    private async Task<AnalysisJob> UpdateAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var updated = job with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAnalysisAsync(updated, cancellationToken);
        return updated;
    }

    private static ReviewNarrative BuildDeterministicNarrative(VersionedGraph graph, IReadOnlyList<ChangedFile> files)
    {
        var additions = graph.Changes.Count(static change => change.Type == SymbolChangeKind.AddSymbol);
        var removals = graph.Changes.Count(static change => change.Type == SymbolChangeKind.RemoveSymbol);
        var modifications = graph.Changes.Count - additions - removals;
        var risks = graph.Changes
            .Where(static change => change.Type is SymbolChangeKind.RemoveSymbol or SymbolChangeKind.ChangeSignature)
            .Select(change => new RiskItem(
                change.Type == SymbolChangeKind.RemoveSymbol ? "high" : "medium",
                change.Type == SymbolChangeKind.RemoveSymbol
                    ? "심볼이 삭제되었습니다. 외부 호출자가 남아 있는지 확인하세요."
                    : "심볼 시그니처가 변경되었습니다. 호출 호환성을 확인하세요.",
                change.EvidenceIds))
            .ToArray();
        return new ReviewNarrative(
            $"{files.Count}개 파일에서 심볼 추가 {additions}개, 삭제 {removals}개, 수정 {modifications}개를 감지했습니다.",
            "두 Git 리비전의 정적 구문 분석 결과를 비교한 근거 기반 요약입니다.",
            risks,
            []);
    }

    private static ReviewNarrative BuildGroupNarrative(
        AnalysisGroupSelection group,
        VersionedGraph graph,
        IReadOnlyList<string> warnings)
    {
        var selected = group.ChangeIds.ToHashSet(StringComparer.Ordinal);
        var changes = graph.Changes.Where(change => selected.Contains(change.Id)).ToArray();
        var risks = changes
            .Where(static change => change.Type is SymbolChangeKind.RemoveSymbol or SymbolChangeKind.ChangeSignature)
            .Select(change => new RiskItem(
                change.Type == SymbolChangeKind.RemoveSymbol ? "high" : "medium",
                change.Type == SymbolChangeKind.RemoveSymbol
                    ? "삭제된 심볼의 호출자를 확인하세요."
                    : "변경된 시그니처의 호출 호환성을 확인하세요.",
                change.EvidenceIds))
            .ToArray();
        return new ReviewNarrative(
            $"'{group.Title}' 그룹의 변경 심볼 {changes.Length}개와 직접 관련된 호출 관계를 표시합니다.",
            $"{string.Join(", ", group.EffectiveViews().Select(static view => $"{view.DiagramType}/{view.PresetId}"))} 형식과 샘플 구성을 적용했습니다.",
            risks,
            warnings);
    }

    private static string MapErrorCode(Exception exception) => exception switch
    {
        GitWorkerException gitWorkerException => gitWorkerException.ErrorCode,
        DiagramGenerationException diagramGenerationException => diagramGenerationException.Code,
        DirectoryNotFoundException => "REPOSITORY_NOT_FOUND",
        TimeoutException or OperationCanceledException => "ANALYSIS_TIMEOUT",
        DiagramValidationException => "DIAGRAM_INVALID",
        _ => "ANALYSIS_FAILED"
    };

    private static string SafeMessage(Exception exception) => exception switch
    {
        GitWorkerException gitWorkerException => gitWorkerException.UserMessage,
        DiagramGenerationException diagramGenerationException => diagramGenerationException.Message,
        DirectoryNotFoundException => "등록된 저장소에 접근할 수 없습니다.",
        OperationCanceledException => "분석 시간이 초과되었거나 취소되었습니다.",
        DiagramValidationException => exception.Message,
        _ => "분석에 실패했습니다. 분석 ID로 내부 서버 로그를 확인하세요."
    };

    private sealed record RenderResult(
        IReadOnlyList<DiagramArtifact> Artifacts,
        IReadOnlyList<DiagramAvailability> Availability,
        IReadOnlyList<AnalysisDiagramGroupResult>? Groups,
        int ExpectedCount);
}
