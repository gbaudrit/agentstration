using Agentstration.Management.Core;
using Agentstration.Application.Work;
using Agentstration.Runtime.Local;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

public sealed class LocalWorkExecutionWorker(
    ILocalWorkExecutionQueue queue,
    WorkItemService workItems,
    AgentManagementService management,
    TimeProvider timeProvider,
    ILogger<LocalWorkExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var execution in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var selected = await management.SelectAgentAsync(execution.Request.Instruction, execution.Request.RequestedAgentId, stoppingToken);
                var started = new WorkExecutionStarted(
                    Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId,
                    timeProvider.GetUtcNow(), selected.Route.AgentId);
                await workItems.ApplyExecutionEventAsync(started, stoppingToken);
                var runtimeResult = await management.ExecuteSelectedAsync(selected, execution.Request.Instruction, stoppingToken);
                var result = new WorkResult(
                    [new WorkResultContent(runtimeResult.Output)],
                    [],
                    new Dictionary<string, string> { ["agentId"] = selected.Route.AgentId },
                    timeProvider.GetUtcNow());
                await workItems.ApplyExecutionEventAsync(new WorkExecutionCompleted(
                    Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId,
                    timeProvider.GetUtcNow(), result), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Local execution {ExecutionId} failed for work item {WorkItemId}", execution.Accepted.ExecutionId.Value, execution.Request.WorkItemId.Value);
                try
                {
                    var error = new WorkError("runtime_execution_failed", "The runtime could not complete the work.", WorkErrorCategory.Execution, true, timeProvider.GetUtcNow(), execution.Accepted.ExecutionId, exception.Message);
                    await workItems.ApplyExecutionEventAsync(new WorkExecutionFailed(
                        Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId,
                        timeProvider.GetUtcNow(), error), stoppingToken);
                }
                catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                {
                    logger.LogError(persistenceException, "Could not persist failure for work item {WorkItemId}", execution.Request.WorkItemId.Value);
                }
            }
        }
    }
}
