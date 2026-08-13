using Agentstration.Infrastructure;
using Agentstration.Application.Work;
using Agentstration.Runtime.Local;
using Agentstration.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using System.Text.Json;

namespace Agentstration.Web.Hosting;

public sealed class LocalWorkExecutionWorker(
    ILocalWorkExecutionQueue queue,
    WorkItemService workItems,
    AgentExecutionCoordinator agentExecution,
    FlowRunService flowRuns,
    TimeProvider timeProvider,
    ILogger<LocalWorkExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var execution in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (execution.Request.Flow is not null)
                {
                    await ExecuteFlowAsync(execution, stoppingToken);
                    continue;
                }
                var selected = await agentExecution.SelectAgentAsync(execution.Request.Instruction, execution.Request.RequestedAgentId, stoppingToken);
                var started = new WorkExecutionStarted(
                    Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId,
                    timeProvider.GetUtcNow(), selected.Route.AgentId);
                await workItems.ApplyExecutionEventAsync(started, stoppingToken);
                var runtimeResult = await agentExecution.ExecuteSelectedAsync(selected, execution.Request.Instruction, stoppingToken);
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

    private async Task ExecuteFlowAsync(LocalWorkExecution execution, CancellationToken cancellationToken)
    {
        var flow = execution.Request.Flow!;
        var selectedAgent = $"flow:{flow.FlowId.Value}";
        await workItems.ApplyExecutionEventAsync(new WorkExecutionStarted(
            Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId,
            timeProvider.GetUtcNow(), selectedAgent), cancellationToken);
        var input = JsonSerializer.SerializeToElement(new
        {
            prompt = execution.Request.Instruction,
            inputs = execution.Request.Inputs.Select(value => value.Structured ?? JsonSerializer.SerializeToElement(value.Text)).ToArray()
        });
        var created = await flowRuns.CreateAsync(
            flow.FlowId, flow.UseActiveVersion ? null : flow.Version, "local", FlowRunTrigger.WorkItem,
            "workplace",
            execution.Request.CorrelationId.Value, input,
            execution.Request.Metadata.GetValueOrDefault("workplace.parentFlowRunId"),
            execution.Request.Metadata.GetValueOrDefault("workplace.interactionId"),
            execution.Request.Metadata.GetValueOrDefault("workplace.taskId") ?? execution.Request.WorkItemId.Value.ToString("D"),
            execution.Request.Metadata.GetValueOrDefault("workplace.triggerMessageId"),
            cancellationToken);
        FlowRun current = created.Value;
        await foreach (var observed in flowRuns.ObserveAsync(created.Value.Id, cancellationToken)) current = observed;
        if (!await WaitUntilTaskCanCompleteAsync(execution.Request.WorkItemId, cancellationToken)) return;
        if (current.Status != FlowRunStatus.Succeeded)
        {
            var error = new WorkError(
                current.Error?.Code ?? "flow_run_failed", current.Error?.Message ?? "The Flow Run failed.",
                WorkErrorCategory.Execution, true, timeProvider.GetUtcNow(), execution.Accepted.ExecutionId, current.Error?.Details);
            await workItems.ApplyExecutionEventAsync(new WorkExecutionFailed(
                Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId, timeProvider.GetUtcNow(), error), cancellationToken);
            return;
        }

        var output = current.Output;
        var text = output is { ValueKind: JsonValueKind.String } ? output.Value.GetString() : null;
        var result = new WorkResult(
            [new WorkResultContent(text, output?.Clone(), output?.ValueKind == JsonValueKind.String ? "text/plain" : "application/json")],
            [],
            new Dictionary<string, string> { ["flowRunId"] = current.Id, ["flowId"] = current.FlowId.Value },
            timeProvider.GetUtcNow());
        await workItems.ApplyExecutionEventAsync(new WorkExecutionCompleted(
            Guid.NewGuid(), execution.Request.WorkItemId, execution.Accepted.ExecutionId, timeProvider.GetUtcNow(), result), cancellationToken);
    }

    private async Task<bool> WaitUntilTaskCanCompleteAsync(WorkItemId workItemId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var stored = await workItems.GetAsync(workItemId, cancellationToken);
            if (stored is null || stored.Value.Status is WorkItemStatus.Cancelled or WorkItemStatus.Completed or WorkItemStatus.Failed) return false;
            if (stored.Value.Status != WorkItemStatus.Paused) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
