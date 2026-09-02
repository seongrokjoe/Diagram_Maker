using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Background;

public sealed class AnalysisPlanWorker(
    IAppStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisPlanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var plan = await store.TryLeaseAnalysisPlanAsync(TimeSpan.FromMinutes(6), stoppingToken);
                if (plan is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AnalysisPlanProcessor>().ProcessAsync(plan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Analysis plan worker loop failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
