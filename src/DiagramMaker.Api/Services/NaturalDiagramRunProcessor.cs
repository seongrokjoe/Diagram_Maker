using DiagramMaker.Domain;
using DiagramMaker.Storage;
using DiagramMaker.Configuration;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class NaturalDiagramRunProcessor(IAppStore store, NaturalDiagramService service, IOptions<LlmOptions> options)
{
    public async Task ProcessAsync(NaturalDiagramRun leased, CancellationToken cancellationToken)
    {
        var run = leased;
        var fingerprint = SemanticExecution.Hash(System.Text.Json.JsonSerializer.Serialize(new {
            run.Request, run.Answers, run.AnswerVersion, NaturalDiagramService.GeneratorVersion,
            policy = SemanticExecution.PolicyFingerprint(options.Value), protocol = NaturalDesignValidation.Protocol }));
        if (run.InputFingerprint is not null && run.InputFingerprint != fingerprint)
            run = run with { Requirements = null, Views = run.Views?.Select(view => view with {
                State = "Failed", Pages = view.Pages?.Select(page => page with { State = "Failed" }).ToArray() }).ToArray() };
        run = run with { InputFingerprint = fingerprint };
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SemanticExecution? execution = null;
        async Task<bool> Save(NaturalDiagramRun updated)
        {
            updated = updated with { Revision = run.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow,
                Checkpoints = execution?.Checkpoints ?? updated.Checkpoints,
                Diagnostics = execution?.Diagnostics ?? updated.Diagnostics, Execution = execution?.Progress ?? updated.Execution };
            if (!await store.UpdateNaturalDiagramRunAsync(updated, run.Revision, run.LeaseId, cancellationToken))
            {
                await leaseLost.CancelAsync();
                return false;
            }
            run = updated;
            return true;
        }

        execution = new SemanticExecution(options.Value, run.Checkpoints, leaseLost.Token, async () =>
        {
            if (!await Save(run with { StageMessage = StageLabel(execution!) }))
                throw new OperationCanceledException(leaseLost.Token);
        }, run.Diagnostics, run.Execution);
        using var executionScope = execution;
        try
        {
            var requirements = await service.PrepareRunRequirementsAsync(run, execution.Token);
            if (!await Save(run with { Requirements = requirements })) return;
            if (run.AnswerVersion == 0 && requirements?.Questions is { Count: > 0 } questions)
            {
                await Save(run with { State = NaturalDiagramRunState.NeedsClarification, Questions = questions,
                    QuestionVersion = Math.Max(1, run.QuestionVersion), StageMessage = "입력 의도를 확인해 주세요.", LeaseId = null, LeaseUntil = null });
                return;
            }
            var record = await service.ExecuteRunAsync(run, async (progress, _) =>
            {
                var percent = progress.TotalUnits <= 0 ? 10 : 10 + 85 * progress.CompletedUnits / progress.TotalUnits;
                if (!await Save(run with { Requirements = progress.Requirements, Views = progress.Views,
                    Progress = Math.Clamp(percent, 10, 95), StageMessage = progress.StageMessage }))
                    throw new OperationCanceledException(leaseLost.Token);
            }, execution.Token);
            var views = record.Views ?? [];
            var state = views.Count > 0 && views.All(view => view.State == "Completed")
                ? NaturalDiagramRunState.Completed : NaturalDiagramRunState.Partial;
            await Save(run with { State = state, Progress = 100,
                StageMessage = state == NaturalDiagramRunState.Completed ? "자연어 다이어그램 생성 완료" : "일부 결과와 실패 진단 저장 완료",
                ResultDiagramId = record.Id, Requirements = record.Requirements, Views = record.Views,
                ErrorCode = null, ErrorMessage = null, LeaseId = null, LeaseUntil = null });
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested) { }
        catch (OperationCanceledException) when (execution.BudgetExpired)
        {
            await Save(run with { State = NaturalDiagramRunState.Partial, StageMessage = "실행 시간 한도에 도달했습니다. 완료된 단위부터 이어갈 수 있습니다.",
                ErrorCode = "NATURAL_EXECUTION_BUDGET", ErrorMessage = $"실행 예산 {execution.BudgetSeconds}초에 도달했습니다.", LeaseId = null, LeaseUntil = null });
        }
        catch (Exception exception)
        {
            var hasResult = run.Views?.SelectMany(view => view.Pages ?? []).Any(page => page.Diagram is not null) == true;
            await Save(run with { State = hasResult ? NaturalDiagramRunState.Partial : NaturalDiagramRunState.Failed,
                Progress = hasResult ? run.Progress : 100,
                StageMessage = hasResult ? "생성이 중단되어 완료된 결과와 진단을 보존했습니다." : "자연어 다이어그램 생성 실패",
                ErrorCode = exception is LlmClientException llmError ? llmError.Code : "NATURAL_DIAGRAM_GENERATION_FAILED",
                ErrorMessage = exception is ArgumentException or InvalidOperationException or LlmClientException
                    ? exception.Message : "자연어 다이어그램 생성 중 내부 오류가 발생했습니다.",
                LeaseId = null, LeaseUntil = null });
        }
    }

    private static string StageLabel(SemanticExecution execution) => execution.Progress.LastRequest?.Purpose == "natural-final-review"
        ? "전체 결과의 누락·모순 검토" : execution.Stage switch
        {
            "llm-NaturalRequirements" => "요구사항 추출·보정",
            "llm-NaturalRequirementsReview" => "원문 근거와 요구사항 검토",
            "llm-NaturalDesign" => "형식별 다이어그램 설계·보정",
            "llm-NaturalDesignReview" => "조건·인터락·흐름 검토",
            "natural-page" => "시나리오 페이지 생성·저장",
            _ => "원문 근거와 시나리오 준비"
        };
}

public sealed class NaturalDiagramWorker(IServiceScopeFactory scopes, IAppStore store,
    ILogger<NaturalDiagramWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var run = await store.TryLeaseNaturalDiagramRunAsync(LeaseDuration, stoppingToken);
                if (run is null) { await Task.Delay(500, stoppingToken); continue; }
                using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = Heartbeat(run, work);
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<NaturalDiagramRunProcessor>().ProcessAsync(run, work.Token);
                }
                finally
                {
                    await work.CancelAsync();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Natural diagram worker could not process a run; retrying without logging prompt content.");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task Heartbeat(NaturalDiagramRun run, CancellationTokenSource work)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(work.Token))
            if (!await store.RenewNaturalDiagramRunLeaseAsync(run.Id, run.LeaseId!.Value, LeaseDuration, work.Token))
            {
                await work.CancelAsync();
                return;
            }
    }
}
