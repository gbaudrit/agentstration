using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Flows;

public sealed class KnowledgeSnapshotRecoveryWorker(
    KnowledgeSnapshotService snapshots,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider,
    ILogger<KnowledgeSnapshotRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        do
        {
            try
            {
                using var requestScope = requestScopes.PushSystem();
                await snapshots.RecoverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge Snapshot publication recovery failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
