using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Hosting;

public sealed class BootstrapApplicationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BootstrapApplicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>()
                    .ProcessNextAsync(stoppingToken);
                if (processed) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Bootstrap application processing failed ({ExceptionType}).", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
