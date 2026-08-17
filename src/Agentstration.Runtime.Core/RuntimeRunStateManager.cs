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
            State = state
        }, cancellationToken);

    private async Task<StoredRuntimeRun> RequiredAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(workspaceId, runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
}
