using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Hosting;

public sealed class RuntimeRunExecutionWorker(
    IRuntimeRunQueue queue,
    RuntimeRunService runs,
    ILogger<RuntimeRunExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await runs.ExecuteAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected runtime run worker failure for {WorkspaceId}/{RunId}", item.Scope.WorkspaceId, item.RunId);
            }
        }
    }
}
