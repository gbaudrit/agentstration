using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public sealed class SourceRefreshWorker(
    SourceRefreshScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<SourceRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await scheduler.RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Source refresh scheduling scan failed");
            }
        }
    }
}
