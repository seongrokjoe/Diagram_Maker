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
    ILogger<AnalysisJobProcessor> logger)
{
    public async Task ProcessAsync(AnalysisJob leasedJob, CancellationToken cancellationToken)
    {
        try
        {
            var repository = await store.GetRepositoryAsync(leasedJob.Request.RepositoryId, cancellationToken)
                             ?? throw new InvalidOperationException("Repository is no longer registered.");

            var comparison = await git.CompareAsync(repository, leasedJob.Request, cancellationToken);
            var job = await UpdateAsync(leasedJob with
            {
                State = AnalysisState.Indexing,
                BaseSha = comparison.BaseSha,
                TargetSha = comparison.TargetSha,
                Progress = 25,
                StageMessage = "Extracting changed symbols"
            }, cancellationToken);

            var graph = analyzer.Analyze(repository.Id, comparison);
            job = await UpdateAsync(job with
            {
                State = AnalysisState.Graphing,
                Progress = 55,
                StageMessage = "Building versioned symbol graph"
            }, cancellationToken);

            var deterministic = BuildDeterministicNarrative(graph, comparison.Files);
            var narrative = deterministic;
            var warnings = deterministic.Warnings.ToList();
            var llmSucceeded = false;
            if (job.Request.IncludeLlmSummary && llm.IsEnabled)
            {
                job = await UpdateAsync(job with
                {
                    State = AnalysisState.Summarizing,
                    Progress = 70,
                    StageMessage = "Requesting evidence-bound internal LLM review"
                }, cancellationToken);
                try
                {
                    var generated = await llm.GenerateReviewAsync(
                        graph,
                        comparison.Files,
                        job.Request.EnableThinking,
                        cancellationToken);
                    if (generated is not null)
                    {
                        narrative = generated;
                        llmSucceeded = true;
                    }
                    else
                    {
                        warnings.Add("Internal LLM returned no valid evidence-bound result.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Internal LLM review failed for analysis {AnalysisId}; deterministic results remain available.", job.Id);
                    warnings.Add("Internal LLM review failed; deterministic analysis is shown.");
                }
            }
            else if (job.Request.IncludeLlmSummary)
            {
                warnings.Add("Internal LLM is disabled; deterministic analysis is shown.");
            }

            narrative = narrative with { Warnings = narrative.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToArray() };
            job = await UpdateAsync(job with
            {
                State = AnalysisState.Rendering,
                Progress = 85,
                StageMessage = "Compiling safe Mermaid diagram"
            }, cancellationToken);

            var diagrams = projection.Build(
                repository.Name,
                graph,
                comparison,
                job.Request.DiagramTypes,
                job.Request.CallerDepth,
                job.Request.CalleeDepth,
                comparison.ContextFilesTruncated);
            var artifacts = diagrams.Artifacts
                .Select(artifact => artifact with { MermaidDsl = compiler.Compile(artifact.Ir) })
                .ToArray();
            var safeFiles = comparison.Files.Select(static file => file with { BeforeContent = null, AfterContent = null }).ToArray();
            var result = new AnalysisResult(safeFiles, graph, narrative, artifacts, diagrams.Availability);
            var finalState = job.Request.IncludeLlmSummary && !llmSucceeded ? AnalysisState.Partial : AnalysisState.Completed;
            await UpdateAsync(job with
            {
                State = finalState,
                Progress = 100,
                StageMessage = finalState == AnalysisState.Completed ? "Analysis completed" : "Static analysis completed with degraded AI summary",
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
            logger.LogError(exception, "Analysis {AnalysisId} failed at stage {State}.", leasedJob.Id, leasedJob.State);
            await store.SaveAnalysisAsync(leasedJob with
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
        var risks = new List<RiskItem>();
        foreach (var change in graph.Changes.Where(static change => change.Type is SymbolChangeKind.RemoveSymbol or SymbolChangeKind.ChangeSignature))
        {
            risks.Add(new RiskItem(
                change.Type == SymbolChangeKind.RemoveSymbol ? "high" : "medium",
                change.Type == SymbolChangeKind.RemoveSymbol ? "A symbol was removed; verify all external callers." : "A symbol signature changed; verify compatibility.",
                change.EvidenceIds));
        }

        return new ReviewNarrative(
            $"{files.Count}개 파일이 변경되었고, 심볼 {additions}개 추가, {removals}개 삭제, {modifications}개 수정이 감지되었습니다.",
            "불변 Git revision과 정적 구문 분석 결과를 기반으로 생성한 결정론적 요약입니다.",
            risks,
            []);
    }

    private static string MapErrorCode(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "REPOSITORY_NOT_FOUND",
        TimeoutException => "ANALYSIS_TIMEOUT",
        DiagramValidationException => "DIAGRAM_INVALID",
        _ => "ANALYSIS_FAILED"
    };

    private static string SafeMessage(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "The registered repository is unavailable.",
        OperationCanceledException => "The analysis timed out or was cancelled.",
        DiagramValidationException => exception.Message,
        _ => "The analysis failed. Consult the internal server log using the analysis ID."
    };
}
