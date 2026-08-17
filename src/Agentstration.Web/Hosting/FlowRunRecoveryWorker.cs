using Agentstration.Flow.Application;

namespace Agentstration.Web.Hosting;

public sealed class FlowRunRecoveryWorker(
    FlowRunService runs,
    TimeProvider timeProvider,
    ILogger<FlowRunRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await runs.InitializeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Flow Run recovery scan failed"); }
        }
    }
}
