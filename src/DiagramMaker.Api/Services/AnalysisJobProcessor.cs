using DiagramMaker.Domain;
using DiagramMaker.Storage;
using DiagramMaker.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DiagramMaker.Services;

public sealed class AnalysisJobProcessor(
    IAppStore store,
    IGitWorkerClient git,
    SourceGraphAnalyzer analyzer,
    IInternalLlmClient llm,
    MermaidCompiler compiler,
    DiagramProjectionService projection,
    DiagramPresetCatalog presets,
    ILogger<AnalysisJobProcessor> logger,
    IOptions<LlmOptions>? options = null)
{
    public async Task ProcessAsync(AnalysisJob leasedJob, CancellationToken cancellationToken)
    {
        var currentJob = leasedJob;
        var parentToken = cancellationToken;
        SemanticExecution? execution = null;
        execution = new SemanticExecution(options?.Value ?? new LlmOptions(), currentJob.Checkpoints, parentToken, async () =>
        {
            var latest = await store.GetAnalysisAsync(currentJob.Id, parentToken);
            if (latest is null || latest.LeaseId != leasedJob.LeaseId) throw new OperationCanceledException();
            currentJob = await UpdateAsync(latest with { Checkpoints = execution!.Checkpoints,
                Diagnostics = execution.Diagnostics, Execution = execution.Progress }, parentToken);
        }, currentJob.Diagnostics) { LeaseId = leasedJob.LeaseId };
        using var executionScope = execution;
        cancellationToken = execution.Token;
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
            currentJob = await UpdateAsync(currentJob with { Result = new AnalysisResult(
                comparison.Files.Select(file => file with { BeforeContent = null, AfterContent = null }).ToArray(),
                graph, deterministic, [], [], []) }, cancellationToken);
            var narrative = deterministic;
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
                    logger.LogWarning(
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
            RenderResult renderResult;
            if (plan is not null && currentJob.Request.Groups is { Count: > 0 })
            {
                renderResult = await RenderGroupsAsync(repository.Name, graph, comparison, currentJob.Request.Groups, warnings,
                    currentJob.Id, sourceResult, currentJob.Request.RequestedViewIds, currentJob.Request.EnableThinking, cancellationToken);
            }
            else
            {
                renderResult = RenderLegacy(repository.Name, graph, comparison, currentJob.Request, warnings, currentJob.Id);
            }
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
            var degraded = renderResult.Artifacts.Count != renderResult.ExpectedCount || (renderResult.Groups ?? [])
                .SelectMany(group => group.Views ?? []).Any(view => view.Document?.Coverage.Any(item => item.State is "Partial" or "Unavailable") == true ||
                    llm.IsEnabled && view.GenerationMetadata?.LlmStatus != "Semantic" || view.Warnings.Any(warning => warning.StartsWith("분석 한도:", StringComparison.Ordinal)));
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
        catch (OperationCanceledException) when (execution.BudgetExpired)
        {
            var latest = await store.GetAnalysisAsync(currentJob.Id, parentToken);
            if (latest is not null && latest.LeaseId == leasedJob.LeaseId && !InMemoryAppStore.IsAnalysisTerminal(latest.State))
                await UpdateAsync(latest with { State = AnalysisState.Partial, StopReason = "budget", LeaseUntil = null,
                    StageMessage = "실행 예산에 도달했습니다. 완료 단위를 재사용하여 이어서 생성할 수 있습니다." }, parentToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("Analysis {AnalysisId} failed at stage {State}.", currentJob.Id, currentJob.State);
            await UpdateAsync(currentJob with
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
            if (plan.IndexVersion is not { } indexVersion ||
                !(indexVersion == SourceGraphAnalyzer.IndexVersion || indexVersion.StartsWith(SourceGraphAnalyzer.IndexVersion + ":", StringComparison.Ordinal)))
            {
                logger.LogInformation(
                    "Re-indexing legacy analysis plan {PlanId} from {IndexVersion} before diagram generation.",
                    plan.Id, plan.IndexVersion);
                var prepared = await git.PrepareAsync(repository, plan.BaseSha!, plan.TargetSha!, cancellationToken);
                if (prepared.Comparison.BaseSha != plan.BaseSha || prepared.Comparison.TargetSha != plan.TargetSha)
                    throw new InvalidOperationException("The immutable analysis plan revisions no longer match.");
                var updatedGraph = analyzer.Analyze(repository.Id, prepared.Comparison, prepared.CppIndex);
                var selectedIds = job.Request.Groups?.SelectMany(group => group.ChangeIds).Distinct().ToArray() ?? [];
                if (selectedIds.Any(id => updatedGraph.Changes.All(change => change.Id != id)))
                    throw new DiagramGenerationException("ANALYSIS_RESELECT_REQUIRED",
                        "분석기 변경으로 일부 변경점 식별자가 달라졌습니다. 새 분석 초안에서 변경점을 다시 선택하세요. 기존 결과는 유지됩니다.");
                return (prepared.Comparison, updatedGraph, plan);
            }
            var planComparison = plan.Comparison;
            // Persisted plans intentionally omit source bodies. Every generation, including
            // redraw and summary-disabled requests, must rehydrate the immutable revisions.
            if (llm.IsEnabled || planComparison.Files.Any(file => file.BeforeContent is null && file.AfterContent is null))
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

        var comparison = await git.CompareAsync(repository, job.Request with { BaseRevision = job.BaseSha ?? job.Request.BaseRevision,
            TargetRevision = job.TargetSha ?? job.Request.TargetRevision }, cancellationToken);
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

    private async Task<RenderResult> RenderGroupsAsync(
        string repositoryName,
        VersionedGraph graph,
        GitComparison comparison,
        IReadOnlyList<AnalysisGroupSelection> groups,
        List<string> warnings,
        Guid analysisId,
        AnalysisResult? sourceResult,
        IReadOnlyList<string>? requestedViewIds,
        bool enableThinking,
        CancellationToken cancellationToken)
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
            var bundle = DiagramEvidenceBuilder.Build(graph, comparison, group.ChangeIds);
            groupWarnings.AddRange(bundle.Warnings);
            var sourceGroup = sourceResult?.DiagramGroups?.FirstOrDefault(item => item.GroupId == group.Id);
            ChangeUnderstanding? understanding = sourceGroup?.BundleHash == bundle.Hash ? sourceGroup.Understanding : null;
            if (llm.IsEnabled && understanding is null)
            {
                await ReportGenerationStageAsync(analysisId, $"{group.Title}: 변경 코드의 의미를 분석하고 있습니다", cancellationToken);
                try { understanding = await llm.UnderstandChangesAsync(bundle, enableThinking, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Change understanding failed for group {GroupId}", group.Id);
                    groupWarnings.Add(LlmFailure.Describe(exception));
                }
            }
            var sourceViews = EffectiveResultViews(sourceGroup)
                .GroupBy(static item => item.ViewId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderByDescending(static item => item.Diagram is not null)
                        .ThenByDescending(static item => item.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                        .First(),
                    StringComparer.Ordinal);
            foreach (var view in group.EffectiveViews())
            {
                expectedCount++;
                sourceViews.TryGetValue(view.Id, out var sourceView);
                var shouldRender = sourceView is null || sourceView.Selection != view ||
                                   sourceView.GenerationMetadata?.BundleHash != bundle.Hash ||
                                   sourceView.GenerationMetadata?.PromptVersion != InternalLlmClient.SemanticPromptVersion ||
                                   requested is null || requested.Contains(view.Id);
                if (!shouldRender && sourceView!.Diagram is not null)
                {
                    var reused = sourceView with { Reused = true };
                    viewResults.Add(reused);
                    artifacts.Add(reused.Diagram);
                    availability.Add(new DiagramAvailability(view.DiagramType, true, null));
                    continue;
                }

                try
                {
                    var preset = presets.Resolve(view.DiagramType, view.PresetId);
                    var effectiveOptions = DiagramEvidenceBuilder.ResolveOptions(preset, view);
                    var projected = projection.Build(
                        repositoryName, graph, comparison, [view.DiagramType],
                        preset.CallerDepth, preset.CalleeDepth, comparison.ContextFilesTruncated,
                        group.ChangeIds.ToHashSet(StringComparer.Ordinal), preset, view.Overrides,
                        view.FocusOnChanges, preserveDetails: true);
                    availability.AddRange(projected.Availability);
                    var artifact = projected.Artifacts.FirstOrDefault();
                    var viewWarnings = new List<string>();
                    var llmStatus = llm.IsEnabled ? "Semantic" : "Disabled";

                    if (artifact is not null)
                    {
                        viewWarnings.AddRange(artifact.Ir.Notes.Where(note => note.StartsWith("분석 한도:", StringComparison.Ordinal)));
                        var document = DiagramDocumentBuilder.Build(artifact, bundle);
                        var pages = new List<DiagramPage>();
                        var instructionResults = new List<string>();
                        var attempts = 0;
                        foreach (var page in document.Pages.OrderBy(p => p.Id == document.OverviewPageId ? 0 : 1))
                        {
                            await ReportGenerationStageAsync(analysisId, $"{group.Title} · {page.Title}: 설계 및 의미 검증", cancellationToken);
                            var pageIr = page.Diagram.Ir;
                            var pageStatus = llm.IsEnabled ? "Deterministic" : "Disabled";
                            var pageWarnings = new List<string>(bundle.Warnings);
                            DiagramExplanation? explanation = null;
                            // Verify fallback before sending to the model. An invalid CFG must not
                            // become a plausible-looking successful diagram through relabeling.
                            _ = compiler.Compile(pageIr);
                            if (llm.IsEnabled)
                            {
                                try
                                {
                                    var previous = sourceView?.Document?.Pages.FirstOrDefault(item => item.Id == page.Id)?.Diagram.Ir;
                                    var generated = await SemanticExecution.RunAsync("git-page", JsonSerializer.Serialize(new { pageIr, bundle.Hash, understanding, view, effectiveOptions, previous, enableThinking }),
                                        () => llm.PlanDiagramAsync(pageIr, bundle, understanding, view with { Overrides = effectiveOptions }, previous, enableThinking, cancellationToken),
                                        value => value.Status == "Semantic");
                                    if (generated is not null)
                                    {
                                        if (generated.Status == "Semantic" && generated.Explanation is null)
                                            generated = generated with { Diagram = pageIr, Status = "Deterministic",
                                                Warnings = [.. generated.Warnings, "페이지 설명의 근거 연결이 없어 정적 결과를 표시합니다."] };
                                        pageIr = generated.Diagram;
                                        pageStatus = generated.Status;
                                        explanation = generated.Explanation;
                                        pageWarnings.AddRange(generated.Warnings);
                                        if (generated.Status != "Semantic") llmStatus = "Deterministic";
                                        viewWarnings.AddRange(generated.Warnings);
                                        instructionResults.AddRange(generated.InstructionResults);
                                        attempts += generated.Attempts;
                                    }
                                    else { llmStatus = "Deterministic"; pageWarnings.Add("LLM 설계 결과가 없어 정적 결과를 표시합니다."); }
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    logger.LogWarning("Diagram planning failed for view {ViewId}, page {PageId}", view.Id, page.Id);
                                    llmStatus = "Deterministic";
                                    pageWarnings.Add(LlmFailure.Describe(exception));
                                }
                            }
                            else pageWarnings.Add("LLM이 비활성화되어 코드 구조만 표시합니다. LLM 연결 후 다시 그리기로 의미 설명을 생성할 수 있습니다.");
                            explanation ??= DiagramExplanationBuilder.Fallback(pageIr, bundle, pageStatus, pageWarnings);
                            explanation = explanation with { Warnings = explanation.Warnings.Concat(pageWarnings).Distinct().ToArray() };
                            viewWarnings.AddRange(pageWarnings);
                            pages.Add(page with { Diagram = page.Diagram with { Ir = pageIr, MermaidDsl = compiler.Compile(pageIr), Explanation = explanation } });
                            var pendingView = new AnalysisDiagramViewResult(view.Id, view, pages[0].Diagram, viewWarnings.ToArray(), "Generating",
                                Document: document with { Pages = pages.ToArray() });
                            var pendingGroup = new AnalysisDiagramGroupResult(group.Id, group.Title, group.ChangeIds, pages[0].Diagram,
                                BuildGroupNarrative(group, graph, groupWarnings), groupWarnings.ToArray(), viewResults.Append(pendingView).ToArray(), understanding, bundle.Hash);
                            var pendingJob = await store.GetAnalysisAsync(analysisId, cancellationToken);
                            if (pendingJob?.Result is not null)
                                await UpdateAsync(pendingJob with { Result = pendingJob.Result with { Diagrams = artifacts.Append(pages[0].Diagram).ToArray(),
                                    DiagramGroups = groupResults.Append(pendingGroup).ToArray() } }, cancellationToken);
                        }
                        if (!llm.IsEnabled) viewWarnings.Add("LLM이 비활성화되어 정적 분석 결과를 표시합니다.");
                        document = DiagramDocumentBuilder.UpdateCoverage(document with { Pages = pages }, bundle);
                        if (document.Coverage.Any(item => item.State is "Partial" or "Unavailable"))
                            viewWarnings.Add("일부 선택 변경은 이 유형에서 표현되지 않았습니다. 변경 커버리지를 확인하세요.");
                        viewWarnings.AddRange(bundle.Warnings);
                        var compiledArtifact = pages.First(page => page.Id == document.OverviewPageId).Diagram;
                        artifacts.Add(compiledArtifact);
                        var metadata = BuildGenerationMetadata(group, view, graph, llmStatus, viewWarnings) with
                        {
                            BundleHash = bundle.Hash, AnalyzerVersion = SourceGraphAnalyzer.IndexVersion,
                            PromptVersion = InternalLlmClient.SemanticPromptVersion, EffectiveOptions = effectiveOptions,
                            InstructionResults = instructionResults.Distinct().ToArray(), Attempts = attempts
                        };
                        viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, compiledArtifact, viewWarnings,
                            "Completed", GenerationMetadata: metadata, Document: document));
                        groupWarnings.AddRange(viewWarnings);
                        continue;
                    }

                    var message = projected.Availability.FirstOrDefault(static item => !item.Available)?.Reason
                                  ?? "선택한 변경점으로 다이어그램을 만들 수 없습니다.";
                    groupWarnings.Add(message);
                    viewResults.Add(new AnalysisDiagramViewResult(view.Id, view, null, [message],
                        "Failed", "DIAGRAM_NO_VALID_OUTPUT", message,
                        GenerationMetadata: BuildGenerationMetadata(group, view, graph, "NotRun", [message])));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
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
            if (understanding is not null) groupNarrative = groupNarrative with
            { Summary = string.Join("\n", understanding.Changes.Select(change => change.Summary)) };
            var compiled = viewResults.Select(static item => item.Diagram).FirstOrDefault(static item => item is not null);
            groupResults.Add(new AnalysisDiagramGroupResult(
                group.Id, group.Title, group.ChangeIds, compiled, groupNarrative, groupWarnings, viewResults, understanding, bundle.Hash));
            warnings.AddRange(groupWarnings.Select(warning => $"[{group.Title}] {warning}"));
        }
        return new RenderResult(artifacts, availability, groupResults, expectedCount);
    }

    private async Task ReportGenerationStageAsync(Guid id, string stage, CancellationToken cancellationToken)
    {
        var current = await store.GetAnalysisAsync(id, cancellationToken);
        if (current is not null) await UpdateAsync(current with { StageMessage = stage }, cancellationToken);
    }

    private static DiagramGenerationMetadata BuildGenerationMetadata(
        AnalysisGroupSelection group,
        DiagramViewSelection view,
        VersionedGraph graph,
        string llmStatus,
        IReadOnlyList<string> warnings)
    {
        var selectedChangeIds = group.ChangeIds.ToHashSet(StringComparer.Ordinal);
        var changes = graph.Changes.Where(change => selectedChangeIds.Contains(change.Id)).ToArray();
        var versionIds = changes
            .SelectMany(static change => new[] { change.BeforeSymbolVersionId, change.AfterSymbolVersionId })
            .Where(static id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        var sources = graph.Versions
            .Where(version => versionIds.Contains(version.Id))
            .Select(static version => new DiagramSourceRange(version.FilePath, version.StartLine, version.EndLine))
            .Distinct()
            .OrderBy(static source => source.FilePath, StringComparer.Ordinal)
            .ThenBy(static source => source.StartLine)
            .ToArray();
        var evidenceIds = changes
            .SelectMany(static change => change.EvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        return new DiagramGenerationMetadata(
            changes.Select(static change => change.Id).ToArray(),
            sources,
            evidenceIds,
            llmStatus,
            string.IsNullOrWhiteSpace(view.RefinementInstruction) ? null : view.RefinementInstruction.Trim(),
            warnings);
    }

    private static IReadOnlyList<AnalysisDiagramViewResult> EffectiveResultViews(AnalysisDiagramGroupResult? group)
    {
        if (group is null) return [];
        if (group.Views is { Count: > 0 }) return group.Views
            .GroupBy(static view => view.ViewId, StringComparer.Ordinal)
            .Select(static views => views.OrderByDescending(static view => view.Diagram is not null)
                .ThenByDescending(static view => view.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                .First())
            .ToArray();
        var selection = new DiagramViewSelection($"{group.GroupId}-view", group.Diagram?.Type ?? "flowchart", "balanced");
        return [new AnalysisDiagramViewResult(selection.Id, selection, group.Diagram, group.Warnings,
            group.Diagram is null ? "Failed" : "Completed")];
    }

    private async Task<AnalysisJob> UpdateAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var latest = await store.GetAnalysisAsync(job.Id, cancellationToken);
        var execution = SemanticExecution.Current;
        if (latest is null || latest.LeaseId != job.LeaseId || latest.LeaseId != execution?.LeaseId || InMemoryAppStore.IsAnalysisTerminal(latest.State))
            throw new OperationCanceledException();
        var updated = job with { UpdatedAt = DateTimeOffset.UtcNow, Revision = latest.Revision + 1,
            Checkpoints = execution?.Checkpoints ?? latest.Checkpoints, Diagnostics = execution?.Diagnostics ?? latest.Diagnostics,
            Execution = execution?.Progress ?? latest.Execution };
        if (!await store.UpdateAnalysisAsync(updated, latest.Revision, cancellationToken)) throw new OperationCanceledException();
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
