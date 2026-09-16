using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed class NaturalDiagramRunProcessor(IAppStore store, NaturalDiagramService service)
{
    public async Task ProcessAsync(NaturalDiagramRun leased, CancellationToken cancellationToken)
    {
        var run = leased;
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task<bool> Save(NaturalDiagramRun updated)
        {
            updated = updated with { Revision = run.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            if (!await store.UpdateNaturalDiagramRunAsync(updated, run.Revision, run.LeaseId, cancellationToken))
            {
                await leaseLost.CancelAsync();
                return false;
            }
            run = updated;
            return true;
        }

        try
        {
            var record = await service.ExecuteRunAsync(run, async (progress, _) =>
            {
                var percent = progress.TotalUnits <= 0 ? 10 : 10 + 85 * progress.CompletedUnits / progress.TotalUnits;
                if (!await Save(run with { Requirements = progress.Requirements, Views = progress.Views,
                    Progress = Math.Clamp(percent, 10, 95), StageMessage = progress.StageMessage }))
                    throw new OperationCanceledException(leaseLost.Token);
            }, leaseLost.Token);
            var views = record.Views ?? [];
            var state = views.Count > 0 && views.All(view => view.State == "Completed")
                ? NaturalDiagramRunState.Completed : NaturalDiagramRunState.Partial;
            await Save(run with { State = state, Progress = 100,
                StageMessage = state == NaturalDiagramRunState.Completed ? "자연어 다이어그램 생성 완료" : "일부 결과와 실패 진단 저장 완료",
                ResultDiagramId = record.Id, Requirements = record.Requirements, Views = record.Views,
                ErrorCode = null, ErrorMessage = null, LeaseId = null, LeaseUntil = null });
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested) { }
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
