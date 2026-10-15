using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeRunService(
    IRuntimeRunStore runs,
    IRuntimeRunQueue queue,
    IRuntimeRunExecutionScope executionScopes,
    IAgentRuntimePreparationService preparation,
    IRuntimeAgentResolver agents,
    RuntimeRunStateManager stateManager,
    TimeProvider timeProvider)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Runtime");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await runs.InitializeAsync(cancellationToken);
        const int pageSize = 200;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await runs.ListRecoverableAsync(skip, pageSize, cancellationToken);
            foreach (var key in page)
            {
                var run = await runs.GetAsync(key.WorkspaceId, key.RunId, cancellationToken)
                    ?? throw new RuntimeRunNotFoundException(key.RunId);
                await queue.EnqueueAsync(new RuntimeRunQueueItem(run.Value.Scope, key.RunId), cancellationToken);
            }
            if (page.Count < pageSize) break;
        }
    }

    public async Task<StoredRuntimeRun> CreateAsync(
        RuntimeRunScope scope,
        RuntimeAgentReference agentReference,
        RuntimeRunInput input,
        RuntimeExecutionOptions execution,
        RuntimeRunOrigin origin,
        string initiator,
        CancellationToken cancellationToken)
    {
        Validate(agentReference, input, execution, initiator);
        if (scope.WorkspaceId.Value == Guid.Empty || scope.TenantId == Guid.Empty || scope.PrincipalId == Guid.Empty)
            throw new RuntimeRunValidationException("execution_scope_invalid", "A complete Runtime execution scope is required.");
        ResolvedRuntimeAgent resolved;
        try
        {
            await executionScopes.ValidateAsync(scope, cancellationToken);
            using var requestScope = executionScopes.Enter(scope);
            _ = await preparation.PrepareAsync(agentReference.Namespace, agentReference.ResourceId, agentReference.Version, cancellationToken);
            resolved = await agents.ResolveAsync(agentReference, cancellationToken);
        }
        catch (RuntimeAgentResolutionException exception) { throw new RuntimeRunValidationException(exception.Code, exception.Message); }

        var name = $"run-{Guid.NewGuid():N}";
        var now = timeProvider.GetUtcNow();
        var run = new RuntimeRun
        {
            WorkspaceId = scope.WorkspaceId,
            Scope = scope,
            Id = name,
            Name = name,
            Properties = new RuntimeRunProperties
            {
                Agent = agentReference,
                Input = input,
                Execution = execution,
                Origin = origin,
                Initiator = initiator,
                Scope = scope
            },
            Status = new RuntimeRunStatus
            {
                State = RuntimeRunState.Pending,
                CreatedAt = now,
                ModelProfile = resolved.ModelProfileName
            }
        };
        var stored = await runs.CreateAsync(run, cancellationToken);
        await stateManager.AppendEventAsync(scope.WorkspaceId, stored.Value.Id, RuntimeRunEventKind.RunCreated, "Run created", state: RuntimeRunState.Pending, cancellationToken: cancellationToken);
        await queue.EnqueueAsync(new RuntimeRunQueueItem(scope, stored.Value.Id), cancellationToken);
        return stored;
    }

    public async Task<StoredRuntimeRun> RetryAsync(RuntimeRunScope scope, string runId, CancellationToken cancellationToken)
    {
        var source = await GetRequiredAsync(scope.WorkspaceId, runId, cancellationToken);
        return await CreateAsync(
            scope,
            source.Value.Properties.Agent,
            source.Value.Properties.Input,
            source.Value.Properties.Execution,
            source.Value.Properties.Origin,
            source.Value.Properties.Initiator,
            cancellationToken);
    }

    public Task<StoredRuntimeRun?> GetAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) => runs.GetAsync(workspaceId, runId, cancellationToken);

    public Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(WorkspaceId workspaceId, string? agentResourceId, int skip, int take, CancellationToken cancellationToken) =>
        runs.ListAsync(workspaceId, agentResourceId, skip, take, cancellationToken);

    public async Task DeleteAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, runId, cancellationToken);
        if (!stored.Value.Status.State.IsTerminal()) throw new RuntimeRunNotTerminalException(runId, stored.Value.Status.State);
        await runs.DeleteAsync(workspaceId, runId, expectedETag, cancellationToken);
    }

    public async Task<AgentRuntimeReadiness> GetReadinessAsync(string agentResourceId, long generation, CancellationToken cancellationToken)
        => await GetReadinessAsync(ResourceNamespace.Default, agentResourceId, generation, cancellationToken);

    public async Task<AgentRuntimeReadiness> GetReadinessAsync(ResourceNamespace @namespace, string agentResourceId, long generation, CancellationToken cancellationToken)
        => await GetReadinessAsync(@namespace, agentResourceId, null, generation, cancellationToken);

    public async Task<AgentRuntimeReadiness> GetReadinessAsync(ResourceNamespace @namespace, string agentResourceId, ResourceScopeRef? scopeRef, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await agents.ResolveAsync(new RuntimeAgentReference(agentResourceId, generation) { Namespace = @namespace, ScopeRef = scopeRef }, cancellationToken);
            var ready = resolved.Ready;
            return new AgentRuntimeReadiness(
                agentResourceId,
                generation,
                ready,
                ready ? "Ready" : resolved.State,
                string.IsNullOrWhiteSpace(resolved.DeploymentId) ? null : resolved.DeploymentId,
                resolved.RevisionId,
                ready ? null : resolved.Error ?? $"Agent revision is {resolved.State}.",
                resolved.RuntimeProfileName,
                resolved.ModelProfileName);
        }
        catch (RuntimeAgentResolutionException exception)
        {
            return new AgentRuntimeReadiness(agentResourceId, generation, false, exception.Code, null, null, exception.Message);
        }
    }

    public async Task<StoredRuntimeRun> CancelAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, runId, cancellationToken);
        if (stored.Value.Status.State.IsTerminal()) return stored;
        await queue.RequestCancellationAsync(new RuntimeRunQueueItem(stored.Value.Scope, runId), cancellationToken);
        StoredRuntimeRun cancelled;
        try
        {
            cancelled = await stateManager.TransitionAsync(stored, RuntimeRunState.Cancelled, null, "Cancelled by the caller.", cancellationToken);
        }
        catch (RuntimeRunConcurrencyException)
        {
            var current = await GetRequiredAsync(workspaceId, runId, cancellationToken);
            if (!current.Value.Status.State.IsTerminal()) throw;
            return current;
        }
        await stateManager.AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.StatusChanged, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
        await stateManager.AppendEventAsync(workspaceId, runId, RuntimeRunEventKind.RunCompleted, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
        return cancelled;
    }

    public async IAsyncEnumerable<RuntimeRunEvent> ObserveAsync(
        WorkspaceId workspaceId,
        string runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = await GetRequiredAsync(workspaceId, runId, cancellationToken);
        var cursor = afterSequence;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await runs.ListEventsAsync(workspaceId, runId, cursor, cancellationToken);
            foreach (var runEvent in events)
            {
                cursor = runEvent.Sequence;
                yield return runEvent;
            }
            var current = await GetRequiredAsync(workspaceId, runId, cancellationToken);
            if (current.Value.Status.State.IsTerminal() && events.Count == 0) yield break;
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RuntimeRunEvent>> ListEventsAsync(
        WorkspaceId workspaceId,
        string runId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        _ = await GetRequiredAsync(workspaceId, runId, cancellationToken);
        return await runs.ListEventsAsync(workspaceId, runId, Math.Max(0, afterSequence), cancellationToken);
    }

    private async Task<StoredRuntimeRun> GetRequiredAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(workspaceId, runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);

    private static void Validate(RuntimeAgentReference agent, RuntimeRunInput input, RuntimeExecutionOptions execution, string initiator)
    {
        if (string.IsNullOrWhiteSpace(agent.ResourceId))
            throw new RuntimeRunValidationException("agent_reference_invalid", "The run must reference an Agent by name.");
        if (agent.Version < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent version must be positive.");
        if (input.Messages.Count == 0 || !input.Messages.Any(message => message.Role == RuntimeMessageRole.User && !string.IsNullOrWhiteSpace(message.Content)))
            throw new RuntimeRunValidationException("input_required", "At least one non-empty user message is required.");
        if (execution.Mode != RuntimeExecutionMode.Interactive) throw new RuntimeRunValidationException("execution_mode_unsupported", "Only interactive execution is currently supported.");
        if (execution.TimeoutSeconds is < 1 or > 600) throw new RuntimeRunValidationException("timeout_invalid", "Timeout must be between 1 and 600 seconds.");
        _ = ParseExecutionOptions(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiator);
    }

    private static ModelExecutionOptions ParseExecutionOptions(RuntimeExecutionOptions execution)
    {
        float? temperature = null;
        int? maxOutputTokens = null;
        foreach (var parameter in execution.Parameters)
        {
            if (string.Equals(parameter.Key, "temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (!parameter.Value.TryGetSingle(out var value) || value is < 0 or > 2)
                    throw new RuntimeRunValidationException("temperature_invalid", "Temperature must be a number between 0 and 2.");
                temperature = value;
            }
            else if (string.Equals(parameter.Key, "maxOutputTokens", StringComparison.OrdinalIgnoreCase))
            {
                if (!parameter.Value.TryGetInt32(out var value) || value <= 0)
                    throw new RuntimeRunValidationException("max_output_tokens_invalid", "MaxOutputTokens must be a positive integer.");
                maxOutputTokens = value;
            }
            else
            {
                throw new RuntimeRunValidationException("runtime_parameter_unsupported", $"Runtime parameter '{parameter.Key}' is not supported.");
            }
        }
        return new ModelExecutionOptions(temperature, maxOutputTokens);
    }
}
