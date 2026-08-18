using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeRunStateManager(IRuntimeRunStore runs, TimeProvider timeProvider)
{
    public async Task CompleteFailureAsync(WorkspaceId workspaceId, string runId, RuntimeRunState state, string error, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await RequiredAsync(workspaceId, runId, cancellationToken);
            if (current.Value.Status.State.IsTerminal()) return;
            try
            {
                await TransitionAsync(current, state, null, error, cancellationToken);
                await AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.Error, error, state: state, cancellationToken: cancellationToken);
                await AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.RunCompleted, error, state: state, cancellationToken: cancellationToken);
                return;
            }
            catch (RuntimeRunConcurrencyException) when (attempt < 2)
            {
                // A concurrent cancellation or completion may already have made the run terminal.
            }
        }
    }

    public Task<StoredRuntimeRun> TransitionAsync(
        StoredRuntimeRun stored,
        RuntimeRunState state,
        string? response,
        string? error,
        CancellationToken cancellationToken,
        string? modelProvider = null,
        string? resolvedModel = null,
        ModelExecutionOptions? effectiveOptions = null)
    {
        var now = timeProvider.GetUtcNow();
        var status = stored.Value.Status with
        {
            State = state,
            StartedAt = state == RuntimeRunState.Running ? stored.Value.Status.StartedAt ?? now : stored.Value.Status.StartedAt,
            CompletedAt = state.IsTerminal() ? now : null,
            Response = response ?? stored.Value.Status.Response,
            Error = error,
            ModelProvider = modelProvider ?? stored.Value.Status.ModelProvider,
            ResolvedModel = resolvedModel ?? stored.Value.Status.ResolvedModel,
            EffectiveTemperature = effectiveOptions?.Temperature ?? stored.Value.Status.EffectiveTemperature,
            EffectiveMaxOutputTokens = effectiveOptions?.MaxOutputTokens ?? stored.Value.Status.EffectiveMaxOutputTokens
        };
        return runs.UpdateAsync(stored.Value with { Status = status }, stored.ETag, cancellationToken);
    }

    public async Task TraceStepAsync(WorkspaceId workspaceId, string runId, string step, CancellationToken cancellationToken)
    {
        await AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.StepStarted, step, step, cancellationToken: cancellationToken);
        await AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.StepCompleted, step, step, cancellationToken: cancellationToken);
    }

    public Task<RuntimeRunEvent> AppendEventAsync(
        WorkspaceId workspaceId,
        string runId,
        RuntimeRunEventKind kind,
        string? message = null,
        string? step = null,
        string? content = null,
        RuntimeRunState? state = null,
        RuntimeToolCall? toolCall = null,
        CancellationToken cancellationToken = default) =>
        runs.AppendEventAsync(new RuntimeRunEvent
        {
            WorkspaceId = workspaceId,
            EventId = Guid.NewGuid(),
            RunId = runId,
            Kind = kind,
            Timestamp = timeProvider.GetUtcNow(),
            Message = message,
            Step = step,
            Content = content,
            State = state,
            ToolCall = toolCall
        }, cancellationToken);

    public async Task<RuntimeToolCall> ProjectToolCallAsync(
        WorkspaceId workspaceId,
        string runId,
        ToolExecutionLifecycleEvent executionEvent,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            var current = await RequiredAsync(workspaceId, runId, cancellationToken);
            var existing = current.Value.Status.ToolCalls.FirstOrDefault(call =>
                string.Equals(call.Id, executionEvent.Context.ToolCallId, StringComparison.Ordinal));
            var projected = Project(existing, executionEvent);
            var calls = current.Value.Status.ToolCalls
                .Where(call => !string.Equals(call.Id, projected.Id, StringComparison.Ordinal))
                .Append(projected)
                .ToArray();
            try
            {
                await runs.UpdateAsync(
                    current.Value with { Status = current.Value.Status with { ToolCalls = calls } },
                    current.ETag,
                    cancellationToken);
                await AppendEventAsync(
                    workspaceId,
                    runId,
                    EventKind(executionEvent),
                    EventMessage(executionEvent),
                    state: projected.State,
                    toolCall: projected,
                    cancellationToken: cancellationToken);
                return projected;
            }
            catch (RuntimeRunConcurrencyException) when (retry < 2)
            {
                // Reload and merge with the concurrent Runtime Run projection.
            }
        }
    }

    private static RuntimeToolCall Project(RuntimeToolCall? current, ToolExecutionLifecycleEvent executionEvent)
    {
        var context = executionEvent.Context;
        return executionEvent switch
        {
            ToolExecutionStarted started => new RuntimeToolCall
            {
                Id = context.ToolCallId,
                InvocationId = context.InvocationId,
                ToolId = context.ToolId,
                Name = context.ToolName,
                State = RuntimeRunState.Running,
                Attempt = (current?.Attempt ?? 0) + 1,
                StartedAt = started.Timestamp,
                ProviderId = context.ToolProviderId,
                ExternalToolId = context.ExternalToolId,
                CorrelationId = context.CorrelationId
            },
            ToolExecutionGovernanceEvaluated governance => Governance(current, governance),
            ToolExecutionCompleted completed => Terminal(
                current,
                completed.Context,
                completed.Timestamp,
                completed.Duration,
                RuntimeRunState.Succeeded,
                null),
            ToolExecutionFailed failed => Terminal(
                current,
                failed.Context,
                failed.Timestamp,
                failed.Duration,
                failed.Cancelled ? RuntimeRunState.Cancelled : RuntimeRunState.Failed,
                failed.ErrorMessage,
                failed.FailureKind,
                failed.ErrorCode),
            _ => throw new ArgumentOutOfRangeException(nameof(executionEvent))
        };
    }

    private static RuntimeToolCall Governance(
        RuntimeToolCall? current,
        ToolExecutionGovernanceEvaluated evaluated) => new()
    {
        Id = evaluated.Context.ToolCallId,
        InvocationId = evaluated.Context.InvocationId,
        ToolId = evaluated.Context.ToolId,
        Name = evaluated.Context.ToolName,
        State = RuntimeRunState.Running,
        Attempt = Math.Max(1, current?.Attempt ?? 0),
        StartedAt = current?.StartedAt ?? evaluated.Timestamp,
        ProviderId = evaluated.Context.ToolProviderId,
        ExternalToolId = evaluated.Context.ExternalToolId,
        CorrelationId = evaluated.Context.CorrelationId,
        Governance = evaluated.Evaluations.ToArray()
    };

    private static RuntimeToolCall Terminal(
        RuntimeToolCall? current,
        ToolExecutionContext context,
        DateTimeOffset completedAt,
        TimeSpan duration,
        RuntimeRunState state,
        string? error,
        ToolExecutionFailureKind? failureKind = null,
        string? errorCode = null) => new()
    {
        Id = context.ToolCallId,
        InvocationId = context.InvocationId,
        ToolId = context.ToolId,
        Name = context.ToolName,
        State = state,
        Attempt = Math.Max(1, current?.Attempt ?? 0),
        StartedAt = current?.StartedAt ?? completedAt - duration,
        CompletedAt = completedAt,
        DurationMilliseconds = duration.TotalMilliseconds,
        ProviderId = context.ToolProviderId,
        ExternalToolId = context.ExternalToolId,
        Error = error,
        FailureKind = failureKind,
        ErrorCode = errorCode,
        CorrelationId = context.CorrelationId,
        Governance = current?.Governance ?? []
    };

    private static RuntimeRunEventKind EventKind(ToolExecutionLifecycleEvent executionEvent) => executionEvent switch
    {
        ToolExecutionStarted => RuntimeRunEventKind.ToolCallStarted,
        ToolExecutionGovernanceEvaluated => RuntimeRunEventKind.ToolCallGovernanceEvaluated,
        ToolExecutionCompleted => RuntimeRunEventKind.ToolCallCompleted,
        ToolExecutionFailed => RuntimeRunEventKind.ToolCallFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(executionEvent))
    };

    private static string EventMessage(ToolExecutionLifecycleEvent executionEvent) => executionEvent switch
    {
        ToolExecutionStarted => "Tool call started",
        ToolExecutionGovernanceEvaluated => "Tool call governance evaluated",
        ToolExecutionCompleted => "Tool call completed",
        ToolExecutionFailed failed when failed.Cancelled => "Tool call cancelled",
        ToolExecutionFailed failed when failed.FailureKind == ToolExecutionFailureKind.Denied => "Tool call denied",
        ToolExecutionFailed => "Tool call failed",
        _ => throw new ArgumentOutOfRangeException(nameof(executionEvent))
    };

    private async Task<StoredRuntimeRun> RequiredAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(workspaceId, runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
}
