using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeRunStateManager(IRuntimeRunStore runs, TimeProvider timeProvider)
{
    public async Task CompleteFailureAsync(string runId, RuntimeRunState state, string error, CancellationToken cancellationToken)
    {
        var current = await RequiredAsync(runId, cancellationToken);
        if (current.Value.Status.State == RuntimeRunState.Cancelled && state == RuntimeRunState.Cancelled) return;
        await TransitionAsync(current, state, null, error, cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.Error, error, state: state, cancellationToken: cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, error, state: state, cancellationToken: cancellationToken);
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
            StartedAt = state == RuntimeRunState.Running ? now : stored.Value.Status.StartedAt,
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

    public async Task TraceStepAsync(string runId, string step, CancellationToken cancellationToken)
    {
        await AppendEventAsync(runId, RuntimeRunEventKind.StepStarted, step, step, cancellationToken: cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.StepCompleted, step, step, cancellationToken: cancellationToken);
    }

    public Task<RuntimeRunEvent> AppendEventAsync(
        string runId,
        RuntimeRunEventKind kind,
        string? message = null,
        string? step = null,
        string? content = null,
        RuntimeRunState? state = null,
        CancellationToken cancellationToken = default) =>
        runs.AppendEventAsync(new RuntimeRunEvent
        {
            EventId = Guid.NewGuid(),
            RunId = runId,
            Kind = kind,
            Timestamp = timeProvider.GetUtcNow(),
            Message = message,
            Step = step,
            Content = content,
            State = state
        }, cancellationToken);

    private async Task<StoredRuntimeRun> RequiredAsync(string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
}
