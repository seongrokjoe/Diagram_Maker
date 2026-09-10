using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class CodeBlockRunProcessor(IAppStore store, CodeBlockAnalyzer analyzer, CodeBlockGroupingService grouping,
    CodeBlockProjectionService projection, IInternalLlmClient llm, MermaidCompiler compiler,
    DiagramPresetCatalog presets, IOptions<LlmOptions> llmOptions)
{
    public async Task ProcessAsync(CodeBlockRun leased, CancellationToken cancellationToken)
    {
        var run = leased;
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SemanticExecution? execution = null;
        async Task<bool> Save(CodeBlockRun updated)
        {
            updated = updated with { Revision = run.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            if (!await store.SaveCodeBlockRunAsync(updated, run.Revision, cancellationToken))
            { leaseLost.Cancel(); return false; }
            run = updated; return true;
        }
        execution = new SemanticExecution(llmOptions.Value, run.Checkpoints, leaseLost.Token, async () =>
        {
            if (!await Save(run with { Checkpoints = execution!.Checkpoints, Diagnostics = execution.Diagnostics, Execution = execution.Progress }))
                throw new OperationCanceledException(leaseLost.Token);
        }, run.Diagnostics);
        using var executionScope = execution;
        var workToken = execution.Token;
        try
        {
            var graph = run.Graph ?? await analyzer.AnalyzeAsync(run.WorkspaceId, run.Snapshot.Blocks, workToken);
            var prepared = grouping.Prepare(run, graph);
            if (!await Save(run with { Graph = graph, Groups = prepared.Groups, Relations = prepared.Relations,
                Questions = prepared.Questions, Warnings = graph.Warnings.Concat(prepared.Warnings).Distinct().ToArray(), Progress = 25,
                State = !run.QuestionsResolved && prepared.Questions.Count > 0 ? CodeBlockRunState.NeedsClarification : CodeBlockRunState.Generating,
                StageMessage = !run.QuestionsResolved && prepared.Questions.Count > 0 ? "관계 확인이 필요합니다." : "코드 동작과 다이어그램 생성",
                LeaseUntil = !run.QuestionsResolved && prepared.Questions.Count > 0 ? null : run.LeaseUntil })) return;
            if (run.State == CodeBlockRunState.NeedsClarification) return;
            if (graph.Symbols.Count == 0) throw new DiagramGenerationException("CODE_BLOCK_EMPTY_ANALYSIS", "분석 가능한 함수·타입·구문이 없습니다. 코드 입력을 보완하세요.");
            var previous = (await store.ListCodeBlockRunsAsync(run.WorkspaceId, 100, cancellationToken)).Where(r => r.Id != run.Id && r.OwnerUserId == run.OwnerUserId && r.Results is { Count: > 0 }).OrderByDescending(r => r.CreatedAt).ToArray();
            var results = new List<CodeBlockGroupResult>();
            var populatedGroups = prepared.Groups.Where(g => g.BlockIds.Count > 0).ToArray();
            var appliedGraph = graph with { Relations = prepared.Relations };
            foreach (var group in populatedGroups)
            {
                workToken.ThrowIfCancellationRequested();
                var groupInput = run.Snapshot with { EnableThinking = group.EnableThinking ?? run.Snapshot.EnableThinking,
                    Relations = prepared.Relations.Where(r => group.BlockIds.Contains(r.FromBlockId) && group.BlockIds.Contains(r.ToBlockId)).ToArray() };
                var availability = projection.Availability(graph, group);
                if (run.RegenerateViewIds is { Count: > 0 } requested && group.Views is { Count: > 0 } knownViews && knownViews.All(v => !requested.Contains(v.Id)))
                {
                    var reusable = previous.SelectMany(r => r.Results ?? []).FirstOrDefault(g => g.GroupId == group.Id &&
                        knownViews.All(v => g.Views.Any(old => old.ViewId == v.Id && old.CacheKey == CacheKey(run, group, v, prepared.Relations))));
                    if (reusable is not null)
                    {
                        results.Add(reusable with { Views = reusable.Views.Select(v => v with { Reused = true }).ToArray() });
                        if (!await Save(run with { Results = results.ToArray(), Progress = 25 + 70 * results.Count / populatedGroups.Length })) return;
                        continue;
                    }
                }
                CodeBlockUnderstanding? understanding = null;
                var warnings = new List<string>();
                string? understandingFailure = null;
                try { understanding = await llm.UnderstandCodeBlocksAsync(groupInput, appliedGraph, group, availability, workToken); }
                catch (Exception e) when (e is LlmClientException or DiagramGenerationException)
                { understandingFailure = "understanding"; warnings.Add(LlmFailure.Describe(e)); }
                var type = understanding?.RecommendedType ?? CodeBlockProjectionService.Recommend(availability, graph, group);
                var selections = group.Views is { Count: > 0 } ? group.Views : [new DiagramViewSelection(StableIds.Create(group.Id, type), type, presets.Resolve(type, "balanced").Id)];
                var views = new List<CodeBlockViewResult>();
                foreach (var selection in selections)
                {
                    var cacheKey = CacheKey(run, group, selection, prepared.Relations);
                    var earlier = previous.SelectMany(r => r.Results ?? []).Where(g => g.GroupId == group.Id).SelectMany(g => g.Views)
                        .Where(v => v.ViewId == selection.Id && v.Pages.Count > 0).ToArray();
                    var old = earlier.FirstOrDefault(v => v.Pages.All(p => p.Diagram.Explanation?.Status == "Semantic")) ?? earlier.FirstOrDefault();
                    if (old?.CacheKey == cacheKey && old.State == "Completed" && !(run.RegenerateViewIds?.Contains(selection.Id) ?? false))
                    { views.Add(old with { Reused = true }); continue; }
                    if (run.RegenerateViewIds is { Count: > 0 } && !run.RegenerateViewIds.Contains(selection.Id) && old?.CacheKey == cacheKey)
                    { views.Add(old with { Reused = true }); continue; }
                    var pages = new List<DiagramPage>();
                    var viewWarnings = warnings.ToList();
                    var semanticComplete = true;
                    string? failureStage = understandingFailure;
                    try
                    {
                        if (!availability.Any(a => a.Type == selection.DiagramType && a.Available))
                            throw new DiagramGenerationException("CODE_BLOCK_UNAVAILABLE", availability.First(a => a.Type == selection.DiagramType).Reason!);
                        var candidates = projection.Build(graph, prepared.Relations, group, selection);
                        foreach (var candidate in candidates.OrderBy(c => c.Level == "summary" || c.Id == "overview" ? 0 : 1))
                        {
                            workToken.ThrowIfCancellationRequested();
                            SemanticGeneration? generated = null;
                            try
                            {
                                generated = await SemanticExecution.RunAsync("code-page", JsonSerializer.Serialize(new { candidate, groupInput, selection }),
                                        () => llm.PlanCodeBlockDiagramAsync(candidate.Diagram, groupInput, appliedGraph, understanding, selection, workToken),
                                        value => value.Status == "Semantic");
                            }
                            catch (Exception e) when (e is LlmClientException or DiagramGenerationException or DiagramValidationException)
                            { failureStage = "llm-request"; viewWarnings.Add(LlmFailure.Describe(e)); }
                            var ir = generated?.Diagram ?? candidate.Diagram;
                            semanticComplete &= generated?.Status == "Semantic";
                            if (generated?.Status != "Semantic") failureStage ??= generated?.FailureStage ?? "llm-configuration";
                            viewWarnings.AddRange(generated?.Warnings ?? [llm.IsEnabled ? "의미 설명 미완료: 코드 이해 또는 응답 검증에 실패했습니다." : "의미 설명 미완료: 내부 LLM 의미 생성이 활성화되지 않았습니다."]);
                            var explanation = generated?.Explanation ?? new DiagramExplanation("의미 설명 미완료 — 정적 코드 근거 결과", [],
                                ir.Nodes.SelectMany(n => n.SourceFactIds ?? []).Distinct().ToArray(), ir.Nodes.SelectMany(n => n.EvidenceIds).Distinct().ToArray(), "Incomplete", viewWarnings.ToArray(), Behaviors: []);
                            pages.Add(new DiagramPage(candidate.Id, candidate.Title, new DiagramArtifact(Guid.NewGuid(), ir.Type, 1, ir, compiler.Compile(ir), DateTimeOffset.UtcNow, explanation),
                                candidate.Level, candidate.BlockIds, candidate.SymbolIds, generated?.Status == "Semantic" ? "semantic" : "static"));
                            var pendingView = new CodeBlockViewResult(selection.Id, selection, "Generating", pages.ToArray(), viewWarnings.Distinct().ToArray(),
                                CacheKey: cacheKey, LlmStatus: semanticComplete ? "Semantic" : "Incomplete", FailureStage: failureStage);
                            var pendingGroup = new CodeBlockGroupResult(group.Id, group.Title, group.BlockIds, views.Append(pendingView).ToArray(), availability);
                            if (!await Save(run with { Results = results.Append(pendingGroup).ToArray(), StageMessage = "검증된 페이지를 저장하며 다음 단위를 생성합니다." })) return;
                        }
                        if (pages.Count == 0) throw new DiagramGenerationException("CODE_BLOCK_NO_PAGES", "생성할 근거 페이지가 없습니다.");
                        if (!semanticComplete && old is { Pages.Count: > 0 } && old.Pages.All(p => p.Diagram.Explanation?.Status == "Semantic"))
                        {
                            views.Add(new CodeBlockViewResult(selection.Id, selection, "Partial", old.Pages, viewWarnings.Distinct().ToArray(),
                                "최신 생성의 의미 검토를 완료하지 못해 이전 성공 결과를 유지합니다.", Reused: true, CacheKey: old.CacheKey, LlmStatus: "Incomplete", FailureStage: failureStage));
                            continue;
                        }
                        views.Add(new CodeBlockViewResult(selection.Id, selection, semanticComplete ? "Completed" : "Partial", pages,
                            viewWarnings.Distinct().ToArray(), semanticComplete ? null : viewWarnings.LastOrDefault(), CacheKey: cacheKey,
                            LlmStatus: semanticComplete ? "Semantic" : "Incomplete", FailureStage: semanticComplete ? null : failureStage));
                    }
                    catch (Exception e) when (e is LlmClientException or DiagramGenerationException or DiagramValidationException)
                    {
                        var message = e is DiagramGenerationException known ? known.Message : "다이어그램 생성 또는 검증을 완료하지 못했습니다.";
                        views.Add(new CodeBlockViewResult(selection.Id, selection, "Failed", old?.Pages ?? [], viewWarnings.ToArray(), message,
                            Reused: old?.Pages.Count > 0, CacheKey: old?.CacheKey, LlmStatus: "Incomplete",
                            FailureStage: e is DiagramGenerationException { Code: "CODE_BLOCK_UNAVAILABLE" } ? "availability" : "projection"));
                    }
                }
                results.Add(new CodeBlockGroupResult(group.Id, group.Title, group.BlockIds, views, availability));
                if (!await Save(run with { Results = results.ToArray(), Progress = 25 + 70 * results.Count / populatedGroups.Length })) return;
            }
            var allViews = results.SelectMany(g => g.Views).ToArray();
            await Save(run with { Results = results, State = allViews.All(v => v.State == "Completed") && graph.Warnings.Count == 0 ? CodeBlockRunState.Completed :
                allViews.All(v => v.Pages.Count == 0) ? CodeBlockRunState.Failed : CodeBlockRunState.Partial,
                Progress = 100, StageMessage = allViews.All(v => v.State == "Completed") ? "의미 다이어그램 생성 완료" :
                    "의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요" });
        }
        catch (OperationCanceledException) when (execution.BudgetExpired)
        {
            await Save(run with { State = CodeBlockRunState.Partial, StopReason = "budget", LeaseUntil = null,
                StageMessage = "실행 예산에 도달했습니다. 저장된 결과에서 이어서 생성할 수 있습니다.",
                Checkpoints = execution.Checkpoints, Diagnostics = execution.Diagnostics, Execution = execution.Progress });
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested) { }
        catch (Exception e)
        {
            await Save(run with { State = CodeBlockRunState.Failed, ErrorCode = e is DiagramGenerationException known ? known.Code : "CODE_BLOCK_GENERATION_FAILED",
                ErrorMessage = e is DiagramGenerationException detail ? detail.Message : "코드 분석 또는 생성에 실패했습니다. 입력과 서버 설정을 확인하세요.",
                StageMessage = "생성 실패" });
        }
    }
    private string CacheKey(CodeBlockRun run, CodeBlockGroupSelection group, DiagramViewSelection selection, IReadOnlyList<CodeBlockRelation> relations) =>
        CodeBlockWorkspaceService.Hash(JsonSerializer.Serialize(new { run.OwnerUserId,
            blocks = run.Snapshot.Blocks.Where(b => group.BlockIds.Contains(b.Id)), group.Id, group.Title, group.BlockIds, selection,
            relations = relations.Where(r => group.BlockIds.Contains(r.FromBlockId) || group.BlockIds.Contains(r.ToBlockId)), run.Answers,
            groupOptionsVersion = 1, enableThinking = group.EnableThinking ?? run.Snapshot.EnableThinking,
            enableUserRelations = group.EnableUserRelations ?? true, CodeBlockAnalyzer.AnalyzerVersion, InternalLlmClient.CodeBlockPromptVersion,
            llmOptions.Value.Model, llmOptions.Value.Endpoint, llmOptions.Value.Enabled, llmOptions.Value.NaturalDiagramTemperature,
            llmOptions.Value.NaturalDiagramSeed, llmOptions.Value.DiagramOutputTokens, llmOptions.Value.ThinkingOutputTokens }));
}

public sealed class CodeBlockWorker(IServiceScopeFactory scopes, IAppStore store, IOptions<CodeBlockOptions> options,
    ILogger<CodeBlockWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var duration = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var run = await store.TryLeaseCodeBlockRunAsync(duration, stoppingToken);
                if (run is null) { await Task.Delay(500, stoppingToken); continue; }
                using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = Heartbeat(run, work, duration);
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CodeBlockRunProcessor>().ProcessAsync(run, work.Token);
                }
                finally { await work.CancelAsync(); try { await heartbeat; } catch (OperationCanceledException) { } }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Code block worker could not process a run; retrying without logging source content."); await Task.Delay(1000, stoppingToken); }
        }
    }
    private async Task Heartbeat(CodeBlockRun run, CancellationTokenSource work, TimeSpan duration)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(work.Token))
            if (!await store.RenewCodeBlockLeaseAsync(run.Id, run.LeaseId!.Value, duration, work.Token))
            { await work.CancelAsync(); return; }
    }
}
