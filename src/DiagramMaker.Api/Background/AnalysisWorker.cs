using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Background;

public sealed class AnalysisWorker(
    IAppStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.TryLeaseAnalysisAsync(TimeSpan.FromMinutes(5), stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<AnalysisJobProcessor>();
                using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = RenewAsync(job.Id, job.LeaseId!.Value, work);
                try { await processor.ProcessAsync(job, work.Token); }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { }
                finally { await work.CancelAsync(); try { await heartbeat; } catch (OperationCanceledException) { } }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Analysis worker loop failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task RenewAsync(Guid id, Guid leaseId, CancellationTokenSource work)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(work.Token))
            if (!await store.RenewAnalysisLeaseAsync(id, leaseId, TimeSpan.FromMinutes(5), work.Token))
            { await work.CancelAsync(); return; }
    }
}
