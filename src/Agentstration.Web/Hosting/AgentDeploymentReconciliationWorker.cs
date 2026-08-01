using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public sealed class AgentDeploymentReconciliationWorker(
    AgentManagementService management,
    IConfiguration configuration,
    ILogger<AgentDeploymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue("Management:ReconciliationIntervalSeconds", 10), 1, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try { await management.ReconcileAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Agent deployment reconciliation iteration failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
