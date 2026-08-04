using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeRunService(
    IRuntimeRunStore runs,
    IRuntimeRunQueue queue,
    IRuntimeRunCancellationRegistry cancellations,
    IControlPlaneStore management,
    IRuntimeRegistry runtimes,
    TimeProvider timeProvider,
    ILogger<RuntimeRunService> logger)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Runtime");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await runs.InitializeAsync(cancellationToken);
        var recoverable = await runs.ListAsync(null, 0, 1000, cancellationToken);
        foreach (var run in recoverable.Where(item => item.Value.Status.State is RuntimeRunState.Pending or RuntimeRunState.Running))
            await queue.EnqueueAsync(run.Value.Id, cancellationToken);
    }

    public async Task<StoredRuntimeRun> CreateAsync(
        RuntimeAgentReference agentReference,
        RuntimeRunInput input,
        RuntimeExecutionOptions execution,
        RuntimeRunOrigin origin,
        string initiator,
        CancellationToken cancellationToken)
    {
        Validate(agentReference, input, execution, initiator);
        var agent = await management.GetAsync<AgentResource>(agentReference.ResourceId, cancellationToken)
            ?? throw new RuntimeRunValidationException("agent_not_found", $"Agent '{agentReference.ResourceId}' was not found.");
        AgentRevision? selectedRevision = null;
        if (agent.Value.Generation != agentReference.Version)
        {
            var revisions = await management.ListAsync<AgentRevision>(AgentstrationResourceTypes.AgentRevisions, agent.Value.ResourceGroup, 0, 1000, cancellationToken);
            selectedRevision = revisions.Select(item => item.Value).FirstOrDefault(revision =>
                string.Equals(revision.AgentResourceId, agentReference.ResourceId, StringComparison.Ordinal) && revision.AgentVersion == agentReference.Version);
            if (selectedRevision is null)
                throw new RuntimeRunValidationException("agent_version_not_found", $"Agent version '{agentReference.Version}' does not exist.");
        }

        var name = $"run-{Guid.NewGuid():N}";
        var now = timeProvider.GetUtcNow();
        var run = new RuntimeRun
        {
            Id = name,
            Name = name,
            ResourceGroup = agent.Value.ResourceGroup!,
            Properties = new RuntimeRunProperties
            {
                Agent = agentReference,
                Input = input,
                Execution = execution,
                Origin = origin,
                Initiator = initiator
            },
            Status = new RuntimeRunStatus
            {
                State = RuntimeRunState.Pending,
                CreatedAt = now,
                ModelProfile = selectedRevision?.Definition.ModelProfileId ?? ResourceIdentifier.Parse(agent.Value.Properties.ModelProfile.ResourceId).Name
            }
        };
        var stored = await runs.CreateAsync(run, cancellationToken);
        await AppendEventAsync(stored.Value.Id, RuntimeRunEventKind.RunCreated, "Run created", state: RuntimeRunState.Pending, cancellationToken: cancellationToken);
        await queue.EnqueueAsync(stored.Value.Id, cancellationToken);
        return stored;
    }

    public async Task<StoredRuntimeRun> RetryAsync(string runId, CancellationToken cancellationToken)
    {
        var source = await GetRequiredAsync(runId, cancellationToken);
        return await CreateAsync(
            source.Value.Properties.Agent,
            source.Value.Properties.Input,
            source.Value.Properties.Execution,
            source.Value.Properties.Origin,
            source.Value.Properties.Initiator,
            cancellationToken);
    }

    public Task<StoredRuntimeRun?> GetAsync(string runId, CancellationToken cancellationToken) => runs.GetAsync(runId, cancellationToken);

    public Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(string? agentResourceId, int skip, int take, CancellationToken cancellationToken) =>
        runs.ListAsync(agentResourceId, skip, take, cancellationToken);

    public async Task<AgentRuntimeReadiness> GetReadinessAsync(string agentResourceId, long generation, CancellationToken cancellationToken)
    {
        var agent = await management.GetAsync<AgentResource>(agentResourceId, cancellationToken);
        if (agent is null)
            return new AgentRuntimeReadiness(agentResourceId, generation, false, "AgentNotFound", null, null, $"Agent '{agentResourceId}' was not found.");

        var deployments = await management.ListAsync<AgentDeployment>(AgentstrationResourceTypes.Deployments, agent.Value.ResourceGroup, 0, 1000, cancellationToken);
        foreach (var deployment in deployments)
        {
            var revision = await management.GetAsync<AgentRevision>(deployment.Value.RevisionId, cancellationToken);
            if (revision is null
                || !string.Equals(revision.Value.AgentResourceId, agentResourceId, StringComparison.Ordinal)
                || revision.Value.AgentVersion != generation)
                continue;

            var registered = runtimes.TryGet(deployment.Value.Id, out _);
            var ready = deployment.Value.DesiredState == DesiredAgentState.Running
                && deployment.Value.OperationalState == OperationalState.Ready
                && registered;
            var error = ready ? null : deployment.Value.LastError
                ?? (registered ? $"Deployment is {deployment.Value.OperationalState}." : "Runtime instance is not provisioned yet.");
            return new AgentRuntimeReadiness(
                agentResourceId,
                generation,
                ready,
                ready ? "Ready" : deployment.Value.OperationalState.ToString(),
                deployment.Value.Id,
                revision.Value.Id,
                error,
                deployment.Value.RuntimeProfileId,
                deployment.Value.ModelProfileId ?? revision.Value.Definition.ModelProfileId);
        }

        return new AgentRuntimeReadiness(agentResourceId, generation, false, "DeploymentNotFound", null, null, $"Agent generation '{generation}' has no deployment.");
    }

    public async Task<StoredRuntimeRun> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(runId, cancellationToken);
        if (stored.Value.Status.State.IsTerminal()) return stored;
        cancellations.Cancel(runId);
        var cancelled = await TransitionAsync(stored, RuntimeRunState.Cancelled, null, "Cancelled by the caller.", cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
        return cancelled;
    }

    public async Task ExecuteAsync(string runId, CancellationToken stoppingToken)
    {
        var stored = await GetRequiredAsync(runId, stoppingToken);
        if (stored.Value.Status.State.IsTerminal()) return;
        using var activity = ActivitySource.StartActivity("runtime.run.execute", ActivityKind.Internal);
        activity?.SetTag("agentstration.run.id", runId);
        activity?.SetTag("agentstration.agent.id", stored.Value.Properties.Agent.ResourceId);
        activity?.SetTag("agentstration.agent.version", stored.Value.Properties.Agent.Version);
        activity?.SetTag("agentstration.run.origin", stored.Value.Properties.Origin.ToString());
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RunId"] = runId,
            ["AgentId"] = stored.Value.Properties.Agent.ResourceId,
            ["AgentVersion"] = stored.Value.Properties.Agent.Version
        });
        var startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation("Runtime run execution started");
        var runToken = cancellations.Register(runId, stoppingToken);
        using var timeoutTimer = new CancellationTokenSource(TimeSpan.FromSeconds(stored.Value.Properties.Execution.TimeoutSeconds), timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken, timeoutTimer.Token);
        try
        {
            stored = await TransitionAsync(stored, RuntimeRunState.Running, null, null, stoppingToken);
            await AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run started", state: RuntimeRunState.Running, cancellationToken: stoppingToken);

            var agent = await management.GetAsync<AgentResource>(stored.Value.Properties.Agent.ResourceId, timeout.Token)
                ?? throw new RuntimeRunValidationException("agent_not_found", $"Agent '{stored.Value.Properties.Agent.ResourceId}' was not found.");
            await TraceStepAsync(runId, "Agent definition resolved", timeout.Token);
            await TraceStepAsync(runId, "Agent type resolved", timeout.Token);
            await TraceStepAsync(runId, "Model profile resolved", timeout.Token);

            var deployment = await ResolveDeploymentAsync(agent.Value, stored.Value.Properties.Agent.Version, timeout.Token);
            activity?.SetTag("agentstration.deployment.id", deployment.Id);
            activity?.SetTag("agentstration.model.profile", stored.Value.Status.ModelProfile);
            await TraceStepAsync(runId, "Prompt composed", timeout.Token);
            await AppendEventAsync(runId, RuntimeRunEventKind.StepStarted, "Model invocation started", "Model invoked", cancellationToken: timeout.Token);
            var prompt = ComposePrompt(stored.Value.Properties.Input);
            var executionOptions = ParseExecutionOptions(stored.Value.Properties.Execution);
            AgentExecutionResult? execution = null;
            await foreach (var executionEvent in runtimes.ExecuteEventsAsync(
                deployment.Id,
                new AgentExecutionRequest(
                    prompt,
                    runId,
                    executionOptions,
                    new AgentExecutionOptions { Streaming = stored.Value.Properties.Execution.Streaming }),
                timeout.Token))
            {
                switch (executionEvent)
                {
                    case ContentDelta delta:
                        await AppendEventAsync(runId, RuntimeRunEventKind.ResponseDelta, content: delta.Content, cancellationToken: timeout.Token);
                        break;
                    case ExecutionCompleted completed:
                        execution = completed.Result;
                        break;
                    case ExecutionFailed failed:
                        throw new InvalidOperationException(failed.Error.Message);
                }
            }
            if (execution is null) throw new InvalidOperationException("The runtime completed without an execution result.");
            activity?.SetTag("agentstration.model.provider", execution.ProviderType);
            activity?.SetTag("agentstration.model.name", execution.ModelName);
            activity?.SetTag("agentstration.model.temperature", execution.EffectiveOptions?.Temperature);
            activity?.SetTag("agentstration.model.max_output_tokens", execution.EffectiveOptions?.MaxOutputTokens);
            await AppendEventAsync(runId, RuntimeRunEventKind.StepCompleted, "Model invocation completed", "Model invoked", cancellationToken: timeout.Token);
            stored = await GetRequiredAsync(runId, stoppingToken);
            if (stored.Value.Status.State == RuntimeRunState.Cancelled) return;
            await TransitionAsync(
                stored,
                RuntimeRunState.Succeeded,
                execution.Output,
                null,
                stoppingToken,
                execution.ProviderType,
                execution.ModelName,
                execution.EffectiveOptions);
            await AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run succeeded", state: RuntimeRunState.Succeeded, cancellationToken: stoppingToken);
            await AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, "Response completed", state: RuntimeRunState.Succeeded, cancellationToken: stoppingToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Runtime run execution succeeded in {DurationMs} ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            var state = runToken.IsCancellationRequested ? RuntimeRunState.Cancelled : RuntimeRunState.TimedOut;
            activity?.SetStatus(ActivityStatusCode.Error, state.ToString());
            activity?.SetTag("error.type", state.ToString());
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Runtime run execution ended as {RunState} after {DurationMs} ms", state, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            await CompleteFailureAsync(runId, state, state == RuntimeRunState.Cancelled ? "Run cancelled." : "Run timed out.", stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(exception, "Runtime run execution failed after {DurationMs} ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            await CompleteFailureAsync(runId, RuntimeRunState.Failed, exception.Message, stoppingToken);
        }
        finally
        {
            cancellations.Complete(runId);
        }
    }

    public async IAsyncEnumerable<RuntimeRunEvent> ObserveAsync(
        string runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = await GetRequiredAsync(runId, cancellationToken);
        var cursor = afterSequence;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await runs.ListEventsAsync(runId, cursor, cancellationToken);
            foreach (var runEvent in events)
            {
                cursor = runEvent.Sequence;
                yield return runEvent;
            }
            var current = await GetRequiredAsync(runId, cancellationToken);
            if (current.Value.Status.State.IsTerminal() && events.Count == 0) yield break;
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken);
        }
    }

    private async Task<AgentDeployment> ResolveDeploymentAsync(AgentResource agent, long version, CancellationToken cancellationToken)
    {
        var deployments = await management.ListAsync<AgentDeployment>(AgentstrationResourceTypes.Deployments, agent.ResourceGroup, 0, 1000, cancellationToken);
        foreach (var deployment in deployments.Where(item => item.Value.DesiredState == DesiredAgentState.Running && item.Value.OperationalState == OperationalState.Ready))
        {
            var revision = await management.GetAsync<AgentRevision>(deployment.Value.RevisionId, cancellationToken);
            if (revision is not null
                && string.Equals(revision.Value.AgentResourceId, agent.Id, StringComparison.Ordinal)
                && revision.Value.AgentVersion == version)
                return deployment.Value;
        }
        throw new InvalidOperationException($"Agent '{agent.Name}' version '{version}' has no ready deployment.");
    }

    private async Task CompleteFailureAsync(string runId, RuntimeRunState state, string error, CancellationToken cancellationToken)
    {
        var current = await GetRequiredAsync(runId, cancellationToken);
        if (current.Value.Status.State == RuntimeRunState.Cancelled && state == RuntimeRunState.Cancelled) return;
        await TransitionAsync(current, state, null, error, cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.Error, error, state: state, cancellationToken: cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, error, state: state, cancellationToken: cancellationToken);
    }

    private async Task<StoredRuntimeRun> TransitionAsync(
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
        return await runs.UpdateAsync(stored.Value with { Status = status }, stored.ETag, cancellationToken);
    }

    private async Task TraceStepAsync(string runId, string step, CancellationToken cancellationToken)
    {
        await AppendEventAsync(runId, RuntimeRunEventKind.StepStarted, step, step, cancellationToken: cancellationToken);
        await AppendEventAsync(runId, RuntimeRunEventKind.StepCompleted, step, step, cancellationToken: cancellationToken);
    }

    private Task<RuntimeRunEvent> AppendEventAsync(
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

    private async Task<StoredRuntimeRun> GetRequiredAsync(string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);

    private static string ComposePrompt(RuntimeRunInput input)
    {
        var prompt = input.Messages.Last(message => message.Role == RuntimeMessageRole.User).Content;
        return string.IsNullOrWhiteSpace(input.Context) ? prompt : $"{prompt}\n\nContext:\n{input.Context}";
    }

    private static void Validate(RuntimeAgentReference agent, RuntimeRunInput input, RuntimeExecutionOptions execution, string initiator)
    {
        if (!ResourceIdentifier.TryParse(agent.ResourceId, out var identifier)
            || !string.Equals(identifier.ProviderNamespace, AgentstrationProviderNamespaces.Agents, StringComparison.Ordinal)
            || !string.Equals(identifier.ResourceType, "agents", StringComparison.Ordinal))
            throw new RuntimeRunValidationException("agent_reference_invalid", "The run must reference an Agentstration.Agents/agents resource.");
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
