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
            currentJob = await UpdateAsync(currentJob with
            {
                State = AnalysisState.Graphing,
                BaseSha = comparison.BaseSha,
                TargetSha = comparison.TargetSha,
                Progress = 55,
                StageMessage = "Building versioned symbol graph"
            }, cancellationToken);

            var deterministic = BuildDeterministicNarrative(graph, comparison.Files);
            var narrative = deterministic;
            var warnings = deterministic.Warnings.ToList();
            var llmSucceeded = false;
            if (currentJob.Request.IncludeLlmSummary && llm.IsEnabled)
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
            else if (currentJob.Request.IncludeLlmSummary)
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
                ? RenderGroups(repository.Name, graph, comparison, currentJob.Request.Groups, warnings, currentJob.Id)
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
            if (job.Request.IncludeLlmSummary)
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
            catch (DiagramValidationException exception)
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
        Guid analysisId)
    {
        var artifacts = new List<DiagramArtifact>();
        var availability = new List<DiagramAvailability>();
        var groupResults = new List<AnalysisDiagramGroupResult>();
        foreach (var group in groups)
        {
            var groupWarnings = new List<string>();
            DiagramArtifact? compiled = null;
            try
            {
                var preset = presets.Resolve(group.DiagramType, group.PresetId);
                var projected = projection.Build(
                    repositoryName, graph, comparison, [group.DiagramType],
                    preset.CallerDepth, preset.CalleeDepth, comparison.ContextFilesTruncated,
                    group.ChangeIds.ToHashSet(StringComparer.Ordinal), preset, group.Overrides);
                availability.AddRange(projected.Availability);
                if (projected.Artifacts.FirstOrDefault() is { } artifact)
                {
                    compiled = artifact with { MermaidDsl = compiler.Compile(artifact.Ir) };
                    artifacts.Add(compiled);
                }
                else
                {
                    groupWarnings.Add(projected.Availability.FirstOrDefault()?.Reason
                                      ?? "선택한 변경점으로 다이어그램을 만들 수 없습니다.");
                }
            }
            catch (DiagramValidationException exception)
            {
                logger.LogWarning(exception, "Diagram group {GroupId} was rejected for analysis {AnalysisId}.", group.Id, analysisId);
                availability.Add(new DiagramAvailability(group.DiagramType, false, "유효하지 않은 관계가 있어 이 그룹을 제외했습니다."));
                groupWarnings.Add("다이어그램의 노드 또는 관계가 유효하지 않아 이 그룹만 제외했습니다.");
            }
            var groupNarrative = BuildGroupNarrative(group, graph, groupWarnings);
            groupResults.Add(new AnalysisDiagramGroupResult(
                group.Id, group.Title, group.ChangeIds, compiled, groupNarrative, groupWarnings));
            warnings.AddRange(groupWarnings.Select(warning => $"[{group.Title}] {warning}"));
        }
        return new RenderResult(artifacts, availability, groupResults, groups.Count);
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
            $"{group.DiagramType} 형식과 {group.PresetId} 샘플 구성을 적용했습니다.",
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
