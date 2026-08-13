using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeRunService(
    IRuntimeRunStore runs,
    IRuntimeRunQueue queue,
    IRuntimeRunCancellationRegistry cancellations,
    IRuntimeAgentResolver agents,
    IRuntimeRegistry runtimes,
    RuntimeRunStateManager stateManager,
    TimeProvider timeProvider,
    ILogger<RuntimeRunService> logger)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Runtime");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await runs.InitializeAsync(cancellationToken);
        const int pageSize = 200;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await runs.ListAsync(null, skip, pageSize, cancellationToken);
            foreach (var run in page.Where(item => item.Value.Status.State is RuntimeRunState.Pending or RuntimeRunState.Running))
                await queue.EnqueueAsync(run.Value.Id, cancellationToken);
            if (page.Count < pageSize) break;
        }
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
        ResolvedRuntimeAgent resolved;
        try { resolved = await agents.ResolveAsync(agentReference, cancellationToken); }
        catch (RuntimeAgentResolutionException exception) { throw new RuntimeRunValidationException(exception.Code, exception.Message); }

        var name = $"run-{Guid.NewGuid():N}";
        var now = timeProvider.GetUtcNow();
        var run = new RuntimeRun
        {
            Id = name,
            Name = name,
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
                ModelProfile = resolved.ModelProfileName
            }
        };
        var stored = await runs.CreateAsync(run, cancellationToken);
        await stateManager.AppendEventAsync(stored.Value.Id, RuntimeRunEventKind.RunCreated, "Run created", state: RuntimeRunState.Pending, cancellationToken: cancellationToken);
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
        try
        {
            var resolved = await agents.ResolveAsync(new RuntimeAgentReference(agentResourceId, generation), cancellationToken);
            var registered = runtimes.TryGet(resolved.DeploymentId, out _);
            var ready = resolved.Ready && registered;
            return new AgentRuntimeReadiness(
                agentResourceId,
                generation,
                ready,
                ready ? "Ready" : resolved.State,
                resolved.DeploymentId,
                resolved.RevisionId,
                ready ? null : resolved.Error ?? (registered ? $"Deployment is {resolved.State}." : "Runtime instance is not provisioned yet."),
                resolved.RuntimeProfileName,
                resolved.ModelProfileName);
        }
        catch (RuntimeAgentResolutionException exception)
        {
            return new AgentRuntimeReadiness(agentResourceId, generation, false, exception.Code, null, null, exception.Message);
        }
    }

    public async Task<StoredRuntimeRun> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(runId, cancellationToken);
        if (stored.Value.Status.State.IsTerminal()) return stored;
        cancellations.Cancel(runId);
        StoredRuntimeRun cancelled;
        try
        {
            cancelled = await stateManager.TransitionAsync(stored, RuntimeRunState.Cancelled, null, "Cancelled by the caller.", cancellationToken);
        }
        catch (RuntimeRunConcurrencyException)
        {
            var current = await GetRequiredAsync(runId, cancellationToken);
            if (!current.Value.Status.State.IsTerminal()) throw;
            return current;
        }
        await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
        await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, "Run cancelled", state: RuntimeRunState.Cancelled, cancellationToken: cancellationToken);
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
            stored = await stateManager.TransitionAsync(stored, RuntimeRunState.Running, null, null, stoppingToken);
            await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run started", state: RuntimeRunState.Running, cancellationToken: stoppingToken);

            var resolved = await agents.ResolveAsync(stored.Value.Properties.Agent, timeout.Token);
            await stateManager.TraceStepAsync(runId, "Agent definition resolved", timeout.Token);
            await stateManager.TraceStepAsync(runId, "Agent type resolved", timeout.Token);
            await stateManager.TraceStepAsync(runId, "Model profile resolved", timeout.Token);

            if (!resolved.Ready) throw new InvalidOperationException(resolved.Error ?? $"Deployment is {resolved.State}.");
            activity?.SetTag("agentstration.deployment.uid", resolved.DeploymentId);
            activity?.SetTag("agentstration.model.profile", stored.Value.Status.ModelProfile);
            await stateManager.TraceStepAsync(runId, "Prompt composed", timeout.Token);
            await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.StepStarted, "Model invocation started", "Model invoked", cancellationToken: timeout.Token);
            var prompt = ComposePrompt(stored.Value.Properties.Input);
            var executionOptions = ParseExecutionOptions(stored.Value.Properties.Execution);
            AgentExecutionResult? execution = null;
            await foreach (var executionEvent in runtimes.ExecuteEventsAsync(
                resolved.DeploymentId,
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
                        await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.ResponseDelta, content: delta.Content, cancellationToken: timeout.Token);
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
            await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.StepCompleted, "Model invocation completed", "Model invoked", cancellationToken: timeout.Token);
            stored = await GetRequiredAsync(runId, stoppingToken);
            if (stored.Value.Status.State == RuntimeRunState.Cancelled) return;
            await stateManager.TransitionAsync(
                stored,
                RuntimeRunState.Succeeded,
                execution.Output,
                null,
                stoppingToken,
                execution.ProviderType,
                execution.ModelName,
                execution.EffectiveOptions);
            await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.StatusChanged, "Run succeeded", state: RuntimeRunState.Succeeded, cancellationToken: stoppingToken);
            await stateManager.AppendEventAsync(runId, RuntimeRunEventKind.RunCompleted, "Response completed", state: RuntimeRunState.Succeeded, cancellationToken: stoppingToken);
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
            if (state == RuntimeRunState.TimedOut)
                await stateManager.CompleteFailureAsync(runId, state, "Run timed out.", stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(exception, "Runtime run execution failed after {DurationMs} ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            await stateManager.CompleteFailureAsync(runId, RuntimeRunState.Failed, exception.Message, stoppingToken);
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

    private async Task<StoredRuntimeRun> GetRequiredAsync(string runId, CancellationToken cancellationToken) =>
        await runs.GetAsync(runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);

    private static string ComposePrompt(RuntimeRunInput input)
    {
        var prompt = input.Messages.Last(message => message.Role == RuntimeMessageRole.User).Content;
        return string.IsNullOrWhiteSpace(input.Context) ? prompt : $"{prompt}\n\nContext:\n{input.Context}";
    }

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
