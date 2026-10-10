using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
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
        var assignmentSucceeded = assignment.State == RuntimeAssignmentState.Succeeded;
        var cancelled = assignment.State == RuntimeAssignmentState.Cancelled;
        var terminalPayload = TerminalPayload(assignment, runId);
        var functionalFailure = assignmentSucceeded && IsFailed(terminalPayload);
        var succeeded = assignmentSucceeded && !functionalFailure;
        var status = succeeded ? FlowRunStatus.Succeeded : cancelled ? FlowRunStatus.Cancelled : FlowRunStatus.Failed;
        var output = assignmentSucceeded ? Property(terminalPayload, "output") : null;
        var errorCode = assignment.CurrentAttempt?.ErrorCode ?? (cancelled ? "flow_run_cancelled" : "worker_execution_failed");
        if (functionalFailure) errorCode = StringProperty(terminalPayload, "errorCode") ?? "FLOW_FAILED";
        var updated = stored.Value with
        {
            Status = status,
            Output = output,
            OutputName = StringProperty(terminalPayload, "outputName"),
            OutputOutcome = ParseOutcome(terminalPayload),
            Error = succeeded ? null : new FlowRunError(errorCode,
                cancelled ? "The Flow Run was cancelled."
                : functionalFailure ? StringProperty(terminalPayload, "error") ?? "Flow execution failed."
                : "The Runtime Worker could not complete the Flow Run.",
                functionalFailure ? StringProperty(terminalPayload, "errorDetails") : null),
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
                var childFlowRunIds = StringArrayProperty(executionEvent.Payload, "childFlowRunIds");
                var artifactStorageFlowRunId = StringProperty(executionEvent.Payload, "artifactStorageFlowRunId");
                var artifacts = ParseArtifacts(executionEvent.Payload);
                await MutateStepAsync(stored, step.StepDefinitionId, current => current with
                {
                    Status = failed ? FlowStepRunStatus.Failed : FlowStepRunStatus.Succeeded,
                    CompletedAt = executionEvent.OccurredAt,
                    Output = Property(executionEvent.Payload, "output"),
                    SelectedTransition = StringProperty(executionEvent.Payload, "selectedTransition"),
                    AgentResourceId = StringProperty(executionEvent.Payload, "agentResourceId") ?? current.AgentResourceId,
                    AgentVersion = LongProperty(executionEvent.Payload, "agentVersion") ?? current.AgentVersion,
                    ChildFlowRunId = childFlowRunIds.LastOrDefault(value =>
                            !string.Equals(value, artifactStorageFlowRunId, StringComparison.Ordinal))
                        ?? current.ChildFlowRunId,
                    ChildFlowRunIds = Merge(current.ChildFlowRunIds, childFlowRunIds),
                    RepeatIteration = IntProperty(executionEvent.Payload, "repeatIteration") ?? current.RepeatIteration,
                    ToolRoute = ParseToolRoute(executionEvent.Payload) ?? current.ToolRoute,
                    Tools = ParseToolIdentity(executionEvent.Payload) is { } tool ? [tool] : current.Tools,
                    Provider = ParseProviderIdentity(executionEvent.Payload) ?? current.Provider,
                    Artifacts = artifacts.Count > 0 ? artifacts : current.Artifacts,
                    ArtifactStorageFlowRunId = artifactStorageFlowRunId ?? current.ArtifactStorageFlowRunId,
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
            OutputName = StringProperty(payload, "outputName"),
            OutputOutcome = ParseOutcome(payload),
            Error = failed ? new FlowRunError(StringProperty(payload, "errorCode") ?? "child_flow_failed",
                StringProperty(payload, "error") ?? "The child Flow failed.",
                StringProperty(payload, "errorDetails")) : null,
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

    private static JsonElement? TerminalPayload(RuntimeWorkerAssignment assignment, string runId) =>
        assignment.ExecutionEvents.LastOrDefault(value => value.Kind == "RunCompleted"
            && string.Equals(value.RunId, runId, StringComparison.Ordinal))?.Payload;

    private static JsonElement? Property(JsonElement? payload, string name)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty(name, out var property)) return property.Clone();
        foreach (var candidate in value.EnumerateObject())
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                return candidate.Value.Clone();
        return null;
    }

    private static string? StringProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? LongProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static int? IntProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static IReadOnlyList<string> StringArrayProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];

    private static IReadOnlyList<string> Merge(IReadOnlyList<string> current, IReadOnlyList<string> added) =>
        current.Concat(added).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<FlowStepArtifactReference> ParseArtifacts(JsonElement? payload)
    {
        if (Property(payload, "artifacts") is not { ValueKind: JsonValueKind.Array } artifacts) return [];
        var result = new List<FlowStepArtifactReference>();
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var artifactId = StringProperty(artifact, "artifactId");
            var fileName = StringProperty(artifact, "fileName");
            var mediaType = StringProperty(artifact, "mediaType");
            if (string.IsNullOrWhiteSpace(artifactId)
                || string.IsNullOrWhiteSpace(fileName)
                || string.IsNullOrWhiteSpace(mediaType))
                continue;
            result.Add(new(
                artifactId,
                fileName,
                mediaType,
                StringProperty(artifact, "kind") ?? "staged",
                StringProperty(artifact, "storageFlowRunId"),
                StringProperty(artifact, "localArtifactId")));
        }
        return result;
    }

    private static bool IsFailed(JsonElement? payload) =>
        string.Equals(StringProperty(payload, "status"), "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(StringProperty(payload, "outcome"), "error", StringComparison.OrdinalIgnoreCase);

    private static FlowOutputOutcome? ParseOutcome(JsonElement? payload) =>
        StringProperty(payload, "outcome")?.ToLowerInvariant() switch
        {
            "success" => FlowOutputOutcome.Success,
            "error" => FlowOutputOutcome.Error,
            _ => null
        };

    private static FlowToolRouteResolution? ParseToolRoute(JsonElement? payload)
    {
        var route = Property(payload, "toolRoute");
        var tool = Property(payload, "tool");
        if (route is not { ValueKind: JsonValueKind.Object }
            || tool is not { ValueKind: JsonValueKind.Object }
            || GuidProperty(tool, "toolUid") is not { } toolUid
            || LongProperty(tool, "toolGeneration") is not { } generation)
            return null;
        return new(
            RequiredString(route, "toolSetName"),
            ResourceNamespace.Parse(RequiredString(route, "toolSetNamespace")),
            RequiredString(route, "toolSetVersion"),
            RequiredString(route, "capability"),
            RequiredString(route, "route"),
            RequiredString(tool, "toolName"),
            ResourceNamespace.Parse(RequiredString(tool, "toolNamespace")),
            toolUid,
            generation,
            RequiredString(tool, "providerName"),
            ResourceNamespace.Parse(RequiredString(tool, "providerNamespace")));
    }

    private static string? ParseToolIdentity(JsonElement? payload) =>
        Property(payload, "tool") is { ValueKind: JsonValueKind.Object } tool
            ? CatalogId(StringProperty(tool, "toolNamespace"), StringProperty(tool, "toolName")) : null;

    private static string? ParseProviderIdentity(JsonElement? payload) =>
        Property(payload, "tool") is { ValueKind: JsonValueKind.Object } tool
            ? CatalogId(StringProperty(tool, "providerNamespace"), StringProperty(tool, "providerName")) : null;

    private static string? CatalogId(string? @namespace, string? name) => string.IsNullOrWhiteSpace(name) ? null
        : string.IsNullOrWhiteSpace(@namespace) || string.Equals(@namespace, ResourceNamespace.Default.Value, StringComparison.Ordinal)
            ? name : $"{@namespace}/{name}";

    private static Guid? GuidProperty(JsonElement? payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.String } value
            && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static string RequiredString(JsonElement? payload, string name) =>
        StringProperty(payload, name) is { Length: > 0 } value ? value
        : throw new RuntimeExecutionMaterialException("flow_input_request_invalid",
            $"The Worker input request is missing '{name}'.");
}
