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
        await foreach (var runId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await runs.ExecuteAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected runtime run worker failure for {RunId}", runId);
            }
        }
    }
}
