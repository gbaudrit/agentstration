using Agentstration.Knowledge;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Flows;

/// <summary>Durable pending resources are authoritative; the bounded signal only reduces admission latency.</summary>
public sealed class KnowledgeProjectionWorker(KnowledgeProjectionService projections,
    IRequestContextScopeFactory requestScopes, TimeProvider clock, ILogger<KnowledgeProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = requestScopes.PushSystem();
                await projections.RecoverPendingAsync(stoppingToken);
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timer.Token);
                try { await projections.WaitForPendingAsync(wake.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge projection background processing failed");
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
        }
    }
}
