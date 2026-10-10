using Agentstration.DataSources;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Flows;

public sealed class DataSourceAcquisitionRecoveryWorker(
    DataSourceAcquisitionService acquisitions,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider,
    ILogger<DataSourceAcquisitionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RecoverAsync(stoppingToken);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = requestScopes.PushSystem();
            await acquisitions.RecoverPendingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Data Source acquisition recovery scan failed");
        }
    }
}
