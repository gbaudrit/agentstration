using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Infrastructure.Runtime;

public sealed class AwpAssignmentProjection(
    IRuntimeRunStore runtimeRuns,
    IFlowRepository flowRuns,
    IFlowRunEventSink flowEvents,
    TimeProvider timeProvider,
    IOptions<FlowRunExecutionOptions> flowOptions,
    IFlowInputRequestSink? inputRequestSink = null) : IRuntimeAssignmentProjection
{
    public async Task ProjectEventsAsync(RuntimeWorkerAssignment assignment,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events, CancellationToken cancellationToken)
    {
        foreach (var executionEvent in events)
        {
            if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
                await ProjectRuntimeEventAsync(assignment, executionEvent, cancellationToken);
            else
                await ProjectFlowEventAsync(assignment, executionEvent, cancellationToken);
        }
    }

    public async Task ProjectTerminalAsync(RuntimeAssignmentTerminalResult result,
        CancellationToken cancellationToken)
    {
        if (result.Assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun) return;
        await ProjectFlowTerminalAsync(result.Assignment, result.Assignment.TargetRunId, result, cancellationToken);
        const int pageSize = 200;
        for (var skip = 0; ; skip += pageSize)
        {
            var keys = await flowRuns.ListRunKeysAsync(skip, pageSize, cancellationToken);
            foreach (var key in keys.Where(value => value.WorkspaceId == result.Assignment.WorkspaceId))
            {
                var candidate = await flowRuns.GetRunAsync(key.WorkspaceId, key.RunId, cancellationToken);
                if (candidate?.Value.RootFlowRunId == result.Assignment.TargetRunId)
                    await ProjectFlowTerminalAsync(result.Assignment, candidate.Value.Id, result, cancellationToken);
            }
            if (keys.Count < pageSize) break;
        }
    }

    private async Task ProjectFlowTerminalAsync(RuntimeWorkerAssignment assignment, string runId,
        RuntimeAssignmentTerminalResult result, CancellationToken cancellationToken)
    {
        var stored = await flowRuns.GetRunAsync(result.Assignment.WorkspaceId,
            runId, cancellationToken);
        if (stored is null || stored.Value.Status.IsTerminal()
            || stored.Value.Status == FlowRunStatus.WaitingForInput) return;

        var now = timeProvider.GetUtcNow();
        var succeeded = assignment.State == RuntimeAssignmentState.Succeeded;
        var cancelled = assignment.State == RuntimeAssignmentState.Cancelled;
        var status = succeeded ? FlowRunStatus.Succeeded : cancelled ? FlowRunStatus.Cancelled : FlowRunStatus.Failed;
        var output = succeeded ? TerminalOutput(assignment, runId) : null;
        var errorCode = assignment.CurrentAttempt?.ErrorCode ?? (cancelled ? "flow_run_cancelled" : "worker_execution_failed");
        var updated = stored.Value with
        {
            Status = status,
            Output = output,
            Error = succeeded ? null : new FlowRunError(errorCode,
                cancelled ? "The Flow Run was cancelled." : "The Runtime Worker could not complete the Flow Run."),
            CompletedAt = now,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null,
            Steps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.Running
                ? step with
                {
                    Status = cancelled ? FlowStepRunStatus.Cancelled : FlowStepRunStatus.Failed,
                    CompletedAt = now,
                    Error = succeeded ? null : new FlowRunError(errorCode, "The Runtime Worker stopped during this step.")
                }
                : step).ToArray()
        };
        await flowRuns.UpdateRunAsync(updated, stored.ETag, cancellationToken);
        await AppendFlowEventAsync(updated, status switch
        {
            FlowRunStatus.Succeeded => FlowRunEventType.FlowRunCompleted,
            FlowRunStatus.Cancelled => FlowRunEventType.FlowRunCancelled,
            _ => FlowRunEventType.FlowRunFailed
        }, null, succeeded ? output : JsonSerializer.SerializeToElement(updated.Error), now, cancellationToken);
    }

    private async Task ProjectRuntimeEventAsync(RuntimeWorkerAssignment assignment,
        RuntimeAssignmentExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        var kind = executionEvent.Kind switch
        {
            "RunStarted" => RuntimeRunEventKind.StatusChanged,
            "StepStarted" => RuntimeRunEventKind.StepStarted,
            "StepCompleted" => RuntimeRunEventKind.StepCompleted,
            "TurnStarted" => RuntimeRunEventKind.StepStarted,
            "TurnCompleted" => RuntimeRunEventKind.StepCompleted,
            "ResponseDelta" => RuntimeRunEventKind.ResponseDelta,
            "ToolCallStarted" => RuntimeRunEventKind.ToolCallStarted,
            "ToolCallCompleted" => RuntimeRunEventKind.ToolCallCompleted,
            "ToolCallFailed" => RuntimeRunEventKind.ToolCallFailed,
            "Diagnostic" => RuntimeRunEventKind.Metrics,
            _ => (RuntimeRunEventKind?)null
        };
        if (kind is null) return;
        var content = executionEvent.Payload is { } payload && payload.TryGetProperty("content", out var value)
            ? value.GetString() : null;
        await runtimeRuns.AppendEventAsync(new RuntimeRunEvent
        {
            WorkspaceId = assignment.WorkspaceId,
            EventId = executionEvent.EventId,
            RunId = executionEvent.RunId,
            Kind = kind.Value,
            Timestamp = executionEvent.OccurredAt,
            Message = executionEvent.Kind,
            Content = content,
            State = executionEvent.Kind == "RunStarted" ? RuntimeRunState.Running : null
        }, cancellationToken);
    }

    private async Task ProjectFlowEventAsync(RuntimeWorkerAssignment assignment,
        RuntimeAssignmentExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        var stored = await flowRuns.GetRunAsync(assignment.WorkspaceId, executionEvent.RunId, cancellationToken);
        if (stored is null) return;
        var step = executionEvent.StepExecutionId is { } stepId
            ? assignment.StepExecutions.SingleOrDefault(value => value.Id == stepId)
            : null;
        switch (executionEvent.Kind)
        {
            case "RunStarted":
                if (stored.Value.Status is FlowRunStatus.Pending or FlowRunStatus.WaitingForChild)
                    await UpdateFlowAsync(stored, stored.Value with
                    {
                        Status = FlowRunStatus.Running,
                        StartedAt = stored.Value.StartedAt ?? executionEvent.OccurredAt,
                        ExecutionLeaseId = assignment.Id.Value.ToString("N"),
                        ExecutionLeaseExpiresAt = assignment.CurrentAttempt?.LeaseExpiresAt
                    }, FlowRunEventType.FlowRunStarted, null, null, executionEvent.OccurredAt, cancellationToken);
                break;
            case "StepStarted" when step is not null:
                await MutateStepAsync(stored, step.StepDefinitionId, current => current with
                {
                    Status = FlowStepRunStatus.Running,
                    StartedAt = current.StartedAt ?? executionEvent.OccurredAt,
                    Attempt = Math.Max(1, current.Attempt)
                }, FlowRunEventType.StepRunStarted, executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "StepCompleted" when step is not null:
                var failed = executionEvent.Payload is { } completedPayload
                    && completedPayload.TryGetProperty("status", out var completedStatus)
                    && string.Equals(completedStatus.GetString(), "failed", StringComparison.OrdinalIgnoreCase);
                await MutateStepAsync(stored, step.StepDefinitionId, current => current with
                {
                    Status = failed ? FlowStepRunStatus.Failed : FlowStepRunStatus.Succeeded,
                    CompletedAt = executionEvent.OccurredAt,
                    Output = Property(executionEvent.Payload, "output"),
                    SelectedTransition = StringProperty(executionEvent.Payload, "selectedTransition"),
                    AgentResourceId = StringProperty(executionEvent.Payload, "agentResourceId") ?? current.AgentResourceId,
                    AgentVersion = LongProperty(executionEvent.Payload, "agentVersion") ?? current.AgentVersion,
                    Error = failed ? new FlowRunError(StringProperty(executionEvent.Payload, "errorCode") ?? "flow_step_failed",
                        StringProperty(executionEvent.Payload, "error") ?? "The Flow step failed.") : null
                }, failed ? FlowRunEventType.StepRunFailed : FlowRunEventType.StepRunCompleted,
                    executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "ResponseDelta":
                await AppendFlowEventAsync(stored.Value, FlowRunEventType.StepOutputDelta, step?.StepDefinitionId,
                    executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "TurnStarted":
                await AppendFlowEventAsync(stored.Value, FlowRunEventType.ParticipantTurnStarted, step?.StepDefinitionId,
                    executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "TurnCompleted":
                await AppendFlowEventAsync(stored.Value, FlowRunEventType.ParticipantTurnCompleted, step?.StepDefinitionId,
                    executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "RunCompleted":
                await CompleteChildAsync(stored, executionEvent.Payload, executionEvent.OccurredAt, cancellationToken);
                break;
            case "WaitingForInput":
                await SuspendForInputAsync(assignment, stored, executionEvent.Payload,
                    executionEvent.OccurredAt, cancellationToken);
                break;
        }
    }

    private async Task SuspendForInputAsync(RuntimeWorkerAssignment assignment, StoredFlowRun stored,
        JsonElement? payload, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var runtimeRequestId = RequiredString(payload, "runtimeRequestId");
        var stateId = RequiredString(payload, "stateId");
        var runtimeType = RequiredString(payload, "runtimeType");
        if (!assignment.Checkpoints.Any(value => value.CheckpointId == stateId
            && string.Equals(value.CompatibilityKey, assignment.ExecutionMaterialDigest, StringComparison.Ordinal)))
            throw new RuntimeExecutionMaterialException("flow_checkpoint_incompatible",
                "The Flow cannot suspend without a compatible checkpoint persisted by this assignment.");
        var existing = (await flowRuns.ListInputRequestsAsync(stored.Value.WorkspaceId, stored.Value.Id,
            null, cancellationToken)).FirstOrDefault(value => string.Equals(
                value.Value.RuntimeRequestId, runtimeRequestId, StringComparison.Ordinal));
        var request = existing?.Value ?? new InputRequest
        {
            WorkspaceId = stored.Value.WorkspaceId,
            Id = $"input-{Guid.NewGuid():N}",
            RunId = stored.Value.Id,
            RuntimeRequestId = runtimeRequestId,
            Prompt = RequiredString(payload, "prompt"),
            Type = Enum.TryParse<InputRequestType>(StringProperty(payload, "type"), true, out var type)
                ? type : InputRequestType.Text,
            Options = Property(payload, "options") is { ValueKind: JsonValueKind.Array } options
                ? options.EnumerateArray().Select(value => value.GetString()!).ToArray() : [],
            Source = StringProperty(payload, "source"),
            CreatedAt = occurredAt,
            ExpiresAt = occurredAt + flowOptions.Value.InputRequestTimeout
        };
        if (existing is null) await flowRuns.CreateInputRequestAsync(request, cancellationToken);
        var updated = stored.Value with
        {
            Status = FlowRunStatus.WaitingForInput,
            RuntimeState = new(runtimeType, stateId, occurredAt),
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        };
        await UpdateFlowAsync(stored, updated, FlowRunEventType.InputRequested, request.Source,
            JsonSerializer.SerializeToElement(new { request.Id, request.Prompt, request.Type, request.ExpiresAt }),
            occurredAt, cancellationToken);
        if (inputRequestSink is not null)
            await inputRequestSink.PublishRequestedAsync(updated, request, cancellationToken);
    }

    private async Task CompleteChildAsync(StoredFlowRun stored, JsonElement? payload, DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (stored.Value.RootFlowRunId is null || stored.Value.Status.IsTerminal()) return;
        var failed = payload is { } value && value.TryGetProperty("status", out var status)
            && string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase);
        var updated = stored.Value with
        {
            Status = failed ? FlowRunStatus.Failed : FlowRunStatus.Succeeded,
            Output = Property(payload, "output"),
            Error = failed ? new FlowRunError(StringProperty(payload, "errorCode") ?? "child_flow_failed",
                StringProperty(payload, "error") ?? "The child Flow failed.") : null,
            CompletedAt = occurredAt,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        };
        await UpdateFlowAsync(stored, updated, failed ? FlowRunEventType.FlowRunFailed : FlowRunEventType.FlowRunCompleted,
            null, payload, occurredAt, cancellationToken);
    }

    private async Task MutateStepAsync(StoredFlowRun stored, string stepName,
        Func<FlowStepRun, FlowStepRun> mutate, FlowRunEventType eventType, JsonElement? payload,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var updated = stored.Value with
        {
            Steps = stored.Value.Steps.Select(value => value.StepName == stepName ? mutate(value) : value).ToArray()
        };
        await UpdateFlowAsync(stored, updated, eventType, stepName, payload, occurredAt, cancellationToken);
    }

    private async Task UpdateFlowAsync(StoredFlowRun stored, FlowRun updated, FlowRunEventType eventType,
        string? stepId, JsonElement? payload, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await flowRuns.UpdateRunAsync(updated, stored.ETag, cancellationToken);
        await AppendFlowEventAsync(updated, eventType, stepId, payload, occurredAt, cancellationToken);
    }

    private async Task AppendFlowEventAsync(FlowRun run, FlowRunEventType type, string? stepId,
        JsonElement? payload, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var appended = await flowRuns.AppendRunEventAsync(new FlowRunEvent(run.WorkspaceId, run.Id, 0,
            type, stepId, payload?.Clone(), occurredAt), cancellationToken);
        await flowEvents.PublishAsync(appended, cancellationToken);
    }

    private static JsonElement? TerminalOutput(RuntimeWorkerAssignment assignment, string runId)
    {
        var payload = assignment.ExecutionEvents.LastOrDefault(value => value.Kind == "RunCompleted"
            && string.Equals(value.RunId, runId, StringComparison.Ordinal))?.Payload;
        return Property(payload, "output");
    }

    private static JsonElement? Property(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property)
            ? property.Clone() : null;

    private static string? StringProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? LongProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static string RequiredString(JsonElement? payload, string name) =>
        StringProperty(payload, name) is { Length: > 0 } value ? value
        : throw new RuntimeExecutionMaterialException("flow_input_request_invalid",
            $"The Worker input request is missing '{name}'.");
}
