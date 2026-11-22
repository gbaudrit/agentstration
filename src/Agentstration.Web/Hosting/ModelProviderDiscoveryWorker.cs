using Agentstration.Models;

namespace Agentstration.Web.Hosting;

internal sealed class ModelProviderDiscoveryWorker(
    ModelProviderDiscoveryLifecycle discovery, TimeProvider timeProvider,
    ILogger<ModelProviderDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await discovery.ProcessDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Model discovery scan failed ({ExceptionType}); it will be retried.", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }
}
