using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure;
using Agentstration.Runtime.Local;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

public sealed class LocalWorkExecutionWorker(
    ILocalWorkExecutionQueue queue,
    WorkItemService workItems,
    AgentExecutionCoordinator agentExecution,
    FlowRunService flowRuns,
    IRootFlowRunGateway rootFlowRuns,
    IFlowRunExecutionScope executionScopes,
    TimeProvider timeProvider,
    ILogger<LocalWorkExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var execution in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var scope = execution.Request.ExecutionScope
                    ?? throw new WorkValidationException("work_execution_scope_required", "Work execution requires a durable Workspace scope.");
                await executionScopes.ValidateAsync(scope, stoppingToken);
                using var activeExecutionScope = executionScopes.Enter(scope);
                if (execution.Request.Flow is not null)
                {
                    await ExecuteFlowAsync(execution, stoppingToken);
                    continue;
                }
                var selected = await agentExecution.SelectAgentAsync(execution.Request.Instruction, execution.Request.RequestedAgentId, stoppingToken);
                var started = new WorkExecutionStarted(
                    Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId,
                    timeProvider.GetUtcNow(), selected.Route.AgentId);
                await workItems.ApplyExecutionEventAsync(started, stoppingToken);
                var runtimeResult = await agentExecution.ExecuteSelectedAsync(selected, execution.Request.Instruction, stoppingToken);
                var result = new WorkResult(
                    [new WorkResultContent(runtimeResult.Output)],
                    [],
                    new Dictionary<string, string> { ["agentId"] = selected.Route.AgentId },
                    timeProvider.GetUtcNow());
                await workItems.ApplyExecutionEventAsync(new WorkExecutionCompleted(
                    Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId,
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
                        Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId,
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
            Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId,
            timeProvider.GetUtcNow(), selectedAgent), cancellationToken);
        var input = execution.Request.Metadata.ContainsKey(RootFlowSubmissionService.RootRunIdMetadata)
            && execution.Request.Inputs.FirstOrDefault()?.Structured is { } rootInput
                ? rootInput
                : JsonSerializer.SerializeToElement(new
                {
                    prompt = execution.Request.Instruction,
                    inputs = execution.Request.Inputs.Select(value => value.Structured ?? JsonSerializer.SerializeToElement(value.Text)).ToArray()
                });
        var scope = execution.Request.ExecutionScope!;
        FlowRun created;
        if (execution.Request.Metadata.TryGetValue(RootFlowSubmissionService.RootRunIdMetadata, out var rootRunId))
        {
            var origin = Enum.TryParse<FlowInvocationOrigin>(execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.OriginMetadata), out var parsedOrigin)
                ? parsedOrigin
                : FlowInvocationOrigin.Api;
            var trigger = Enum.TryParse<FlowRunTrigger>(execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.TriggerMetadata), out var parsedTrigger)
                ? parsedTrigger
                : TriggerFor(origin);
            var ensured = await rootFlowRuns.EnsureAsync(new RootFlowRunRequest(
                rootRunId,
                flow,
                trigger,
                origin,
                execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.CallerMetadata) ?? execution.Request.RequestedAgentId ?? "local-user",
                execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.CausationMetadata),
                execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.IdempotencyMetadata),
                execution.Request.CorrelationId.Value,
                input,
                execution.Request.WorkItemId,
                bool.TryParse(execution.Request.Metadata.GetValueOrDefault(RootFlowSubmissionService.ActiveReferenceMetadata), out var resolvedFromActiveReference)
                    && resolvedFromActiveReference,
                execution.Request.Metadata.GetValueOrDefault("workplace.parentFlowRunId"),
                execution.Request.Metadata.GetValueOrDefault("workplace.interactionId"),
                execution.Request.Metadata.GetValueOrDefault("workplace.taskId") ?? execution.Request.WorkItemId.Value.ToString("D"),
                execution.Request.Metadata.GetValueOrDefault("workplace.triggerMessageId"),
                scope), cancellationToken);
            created = ensured.Run;
        }
        else
        {
            created = (await flowRuns.CreateAsync(
                flow.FlowId, flow.UseActiveVersion ? null : flow.Version, "local", FlowRunTrigger.WorkItem,
                "workplace",
                execution.Request.CorrelationId.Value, input,
                execution.Request.Metadata.GetValueOrDefault("workplace.parentFlowRunId"),
                execution.Request.Metadata.GetValueOrDefault("workplace.interactionId"),
                execution.Request.Metadata.GetValueOrDefault("workplace.taskId") ?? execution.Request.WorkItemId.Value.ToString("D"),
                execution.Request.Metadata.GetValueOrDefault("workplace.triggerMessageId"),
                scope,
                cancellationToken)).Value;
        }
        FlowRun current = created;
        await foreach (var observed in flowRuns.ObserveAsync(created.Id, scope, cancellationToken)) current = observed;
        if (!await WaitUntilTaskCanCompleteAsync(execution.Request.WorkspaceId, execution.Request.WorkItemId, cancellationToken)) return;
        if (current.Status != FlowRunStatus.Succeeded)
        {
            var error = new WorkError(
                current.Error?.Code ?? "flow_run_failed", current.Error?.Message ?? "The Flow Run failed.",
                WorkErrorCategory.Execution, true, timeProvider.GetUtcNow(), execution.Accepted.ExecutionId, current.Error?.Details);
            await workItems.ApplyExecutionEventAsync(new WorkExecutionFailed(
                Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId, timeProvider.GetUtcNow(), error), cancellationToken);
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
            Guid.NewGuid(), execution.Request.WorkspaceId, execution.Request.WorkItemId, execution.Accepted.ExecutionId, timeProvider.GetUtcNow(), result), cancellationToken);
    }

    private static FlowRunTrigger TriggerFor(FlowInvocationOrigin origin) => origin switch
    {
        FlowInvocationOrigin.Trigger => FlowRunTrigger.Schedule,
        FlowInvocationOrigin.Api or FlowInvocationOrigin.Console or FlowInvocationOrigin.Mcp or FlowInvocationOrigin.Agent => FlowRunTrigger.Api,
        _ => FlowRunTrigger.WorkItem
    };

    private async Task<bool> WaitUntilTaskCanCompleteAsync(Agentstration.Resources.WorkspaceId workspaceId, WorkItemId workItemId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var stored = await workItems.GetAsync(workspaceId, workItemId, cancellationToken);
            if (stored is null || stored.Value.Status is WorkItemStatus.Cancelled or WorkItemStatus.Completed or WorkItemStatus.Failed) return false;
            if (stored.Value.Status is not (WorkItemStatus.Paused or WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
