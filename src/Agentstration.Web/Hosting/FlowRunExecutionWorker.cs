using Agentstration.Flow.Application;

namespace Agentstration.Web.Hosting;

public sealed class FlowRunExecutionWorker(IFlowRunQueue queue, FlowRunService runs, ILogger<FlowRunExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.ReadAllAsync(stoppingToken))
        {
            try { await runs.ExecuteAsync(runId, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Unexpected Flow Run worker failure for {RunId}", runId); }
        }
    }
}
