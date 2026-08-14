using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;

namespace Agentstration.Flow.Application;

public sealed record FlowAgentExecutionResult(
    JsonElement Output,
    string AgentResourceId,
    long AgentVersion,
    string? ModelProfileResourceId,
    string? Provider,
    FlowStepRunUsage? Usage,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Logs);

public interface IFlowAgentExecutor
{
    Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken);
}

public interface IFlowRunQueue
{
    ValueTask EnqueueAsync(string runId, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IFlowRunCancellationRegistry
{
    CancellationToken Register(string runId, CancellationToken stoppingToken);
    bool Cancel(string runId);
    void Complete(string runId);
}

public interface IFlowRunEventSink
{
    Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken);
}

public sealed class NullFlowRunEventSink : IFlowRunEventSink
{
    public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record FlowRunExecutionOptions
{
    public TimeSpan OrchestrationTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed class FlowRunService(
    IFlowRepository repository,
    IFlowRunQueue queue,
    IFlowRunCancellationRegistry cancellations,
    IFlowAgentExecutor agents,
    IFlowOrchestrationEngine orchestrations,
    IExpressionParser expressionParser,
    IExpressionEvaluator expressions,
    IFlowRunEventSink eventSink,
    TimeProvider timeProvider,
    FlowRunExecutionOptions? executionOptions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FlowRunExecutionOptions executionOptions = executionOptions is null
        ? new()
        : executionOptions.OrchestrationTimeout > TimeSpan.Zero
            ? executionOptions
            : throw new ArgumentOutOfRangeException(nameof(executionOptions), "The orchestration timeout must be positive.");
    public static readonly ActivitySource ActivitySource = new("Agentstration.Flow");
    public static readonly Meter Meter = new("Agentstration.Flow");
    private static readonly Counter<long> RunsCreated = Meter.CreateCounter<long>("agentstration.flow.runs.created");
    private static readonly Counter<long> RunsCompleted = Meter.CreateCounter<long>("agentstration.flow.runs.completed");
    private static readonly Counter<long> RunsFailed = Meter.CreateCounter<long>("agentstration.flow.runs.failed");
    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("agentstration.flow.run.duration", "s");
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var page = await repository.ListRunsAsync(null, null, 0, 1000, cancellationToken);
        foreach (var run in page.Items.Where(item => item.Value.Status is FlowRunStatus.Pending or FlowRunStatus.Running))
            await queue.EnqueueAsync(run.Value.Id, cancellationToken);
    }

    public async Task<StoredFlowRun> CreateAsync(
        FlowId flowId,
        string? version,
        string? deploymentResourceId,
        FlowRunTrigger trigger,
        string? startedBy,
        string? correlationId,
        JsonElement input,
        CancellationToken cancellationToken)
        => await CreateAsync(flowId, version, deploymentResourceId, trigger, startedBy, correlationId, input, null, null, null, null, cancellationToken);

    public async Task<StoredFlowRun> CreateAsync(
        FlowId flowId,
        string? version,
        string? deploymentResourceId,
        FlowRunTrigger trigger,
        string? startedBy,
        string? correlationId,
        JsonElement input,
        string? parentFlowRunId,
        string? interactionId,
        string? workTaskId,
        string? triggerMessageId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveVersionAsync(flowId, version, cancellationToken);
        ValidateInput(resolved.Graph?.InputSchema, input);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            Id = $"flowrun-{Guid.NewGuid():N}",
            FlowId = flowId,
            FlowVersion = resolved.Version,
            DeploymentResourceId = string.IsNullOrWhiteSpace(deploymentResourceId) ? "local" : deploymentResourceId,
            Trigger = trigger,
            StartedBy = string.IsNullOrWhiteSpace(startedBy) ? "local-user" : startedBy,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            ParentFlowRunId = parentFlowRunId,
            InteractionId = interactionId,
            WorkTaskId = workTaskId,
            TriggerMessageId = triggerMessageId,
            Input = input.Clone(),
            CreatedAt = now,
            DefinitionSnapshot = resolved,
            DefinitionHash = resolved.DefinitionHash,
            DefinitionState = FlowDefinitionState.Published,
            DefinitionSnapshotId = $"{flowId.Value}:{resolved.Version}:{resolved.DefinitionHash ?? "legacy"}",
            Steps = CreateSteps(resolved, input)
        };
        var stored = await repository.CreateRunAsync(run, cancellationToken);
        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.Id, FlowRunEventType.FlowRunCreated, null, JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState }), cancellationToken);
        await queue.EnqueueAsync(run.Id, cancellationToken);
        return stored;
    }

    public async Task<StoredFlowRun> CreateDraftAsync(FlowDraft draft, FlowRunTrigger trigger, string? startedBy, string? correlationId, JsonElement input, CancellationToken cancellationToken)
    {
        ValidateInput(draft.Definition.InputSchema, input);
        var validationVersion = $"0.0.0-draft.{draft.Revision}";
        var snapshot = new FlowVersion(draft.FlowId, validationVersion, draft.Description, FlowDraftSnapshotAdapter.ToRoutingDefinition(draft.Definition), draft.Tags,
            timeProvider.GetUtcNow(), draft.Definition, draft.DefinitionHash);
        var now = timeProvider.GetUtcNow();
        var run = new FlowRun
        {
            Id = $"flowrun-{Guid.NewGuid():N}",
            FlowId = draft.FlowId,
            FlowVersion = validationVersion,
            DefinitionState = FlowDefinitionState.Draft,
            DraftRevision = draft.Revision,
            DefinitionHash = draft.DefinitionHash,
            DefinitionSnapshotId = $"snapshot-{Guid.NewGuid():N}",
            DeploymentResourceId = "designer",
            Trigger = trigger,
            StartedBy = string.IsNullOrWhiteSpace(startedBy) ? "local-user" : startedBy,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            Input = input.Clone(),
            CreatedAt = now,
            DefinitionSnapshot = snapshot,
            Steps = CreateSteps(snapshot, input)
        };
        var stored = await repository.CreateRunAsync(run, cancellationToken);
        RunsCreated.Add(1, new KeyValuePair<string, object?>("flow.definition.state", run.DefinitionState.ToString()));
        await EmitAsync(run.Id, FlowRunEventType.FlowRunCreated, null, JsonSerializer.SerializeToElement(new { run.Status, run.DefinitionState, run.DraftRevision }), cancellationToken);
        await queue.EnqueueAsync(run.Id, cancellationToken);
        return stored;
    }

    public Task<StoredFlowRun?> GetAsync(string runId, CancellationToken cancellationToken) => repository.GetRunAsync(runId, cancellationToken);

    public Task<FlowRunPage> ListAsync(FlowId? flowId, FlowRunStatus? status, int skip, int take, CancellationToken cancellationToken) =>
        repository.ListRunsAsync(flowId, status, skip, take, cancellationToken);

    public async Task<StoredFlowRun> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(runId, cancellationToken);
        if (stored.Value.Status.IsTerminal()) return stored;
        cancellations.Cancel(runId);
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.Status is FlowStepRunStatus.NotStarted or FlowStepRunStatus.Running
            ? step with { Status = FlowStepRunStatus.Cancelled, CompletedAt = now }
            : step).ToArray();
        var cancelled = await repository.UpdateRunAsync(stored.Value with
        {
            Status = FlowRunStatus.Cancelled,
            CompletedAt = now,
            Error = new FlowRunError("flow_run_cancelled", "The Flow Run was cancelled."),
            Steps = steps
        }, stored.ETag, cancellationToken);
        await EmitAsync(runId, FlowRunEventType.FlowRunCancelled, null, null, cancellationToken);
        return cancelled;
    }

    public async Task ExecuteAsync(string runId, CancellationToken stoppingToken)
    {
        var stored = await RequiredAsync(runId, stoppingToken);
        if (stored.Value.Status.IsTerminal()) return;
        using var activity = ActivitySource.StartActivity("flow.run.execute", ActivityKind.Internal);
        activity?.SetTag("flow.id", stored.Value.FlowId.Value);
        activity?.SetTag("flow.run.id", runId);
        activity?.SetTag("flow.version", stored.Value.FlowVersion);
        activity?.SetTag("flow.definition.state", stored.Value.DefinitionState.ToString());
        var runToken = cancellations.Register(runId, stoppingToken);
        using var timeout = stored.Value.DefinitionSnapshot.Definition is OrchestrationFlowDefinition
            ? new CancellationTokenSource(executionOptions.OrchestrationTimeout, timeProvider)
            : null;
        using var executionTimeoutLink = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(runToken, timeout.Token);
        var executionToken = executionTimeoutLink?.Token ?? runToken;
        try
        {
            stored = await SaveAsync(stored, stored.Value with { Status = FlowRunStatus.Running, StartedAt = timeProvider.GetUtcNow() }, stoppingToken);
            await EmitAsync(runId, FlowRunEventType.FlowRunStarted, null, null, stoppingToken);
            if (stored.Value.DefinitionSnapshot.Graph is not null)
            {
                await ExecuteGraphAsync(stored, stoppingToken, runToken);
                return;
            }
            if (stored.Value.DefinitionSnapshot.Definition is OrchestrationFlowDefinition orchestration)
            {
                await ExecuteOrchestrationAsync(stored, orchestration, stoppingToken, executionToken);
                return;
            }
            stored = await CompleteSimpleStepAsync(stored, "Input", stored.Value.Input, null, runToken);

            var target = SelectTarget(stored.Value.DefinitionSnapshot.Definition, stored.Value.Input);
            if (stored.Value.DefinitionSnapshot.Definition is RoutingFlowDefinition)
            {
                stored = await CompleteSimpleStepAsync(stored, "Router", JsonSerializer.SerializeToElement(new { selectedAgent = target.Id }), target.Id, runToken);
            }

            stored = await StartStepAsync(stored, "Agent", runToken);
            var execution = await agents.ExecuteAsync(target, stored.Value.Input, stored.Value.CorrelationId!, runToken);
            stored = await FinishAgentStepAsync(stored, execution, runToken);
            stored = await CompleteSimpleStepAsync(stored, "Output", execution.Output, null, runToken);
            var completedAt = timeProvider.GetUtcNow();
            await SaveAsync(stored, stored.Value with { Status = FlowRunStatus.Succeeded, Output = execution.Output.Clone(), CompletedAt = completedAt }, stoppingToken);
            RecordCompletion(stored.Value.CreatedAt, completedAt, stored.Value.DefinitionState);
            await EmitAsync(runId, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true && !runToken.IsCancellationRequested)
        {
            await FailAsync(runId, FlowRunStatus.TimedOut, "flow_run_timed_out", "The Flow Run exceeded its execution timeout.", stoppingToken);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await FailAsync(runId, FlowRunStatus.Cancelled, "flow_run_cancelled", "The Flow Run was cancelled.", stoppingToken);
        }
        catch (FlowValidationException exception)
        {
            await FailAsync(runId, FlowRunStatus.Failed, exception.Code, exception.Message, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(runId, FlowRunStatus.Failed, "flow_run_execution_failed", "The Flow Run could not complete.", stoppingToken, exception.Message);
        }
        finally
        {
            cancellations.Complete(runId);
        }
    }

    public async IAsyncEnumerable<FlowRun> ObserveAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? etag = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var stored = await RequiredAsync(runId, cancellationToken);
            if (!string.Equals(etag, stored.ETag, StringComparison.Ordinal))
            {
                etag = stored.ETag;
                yield return stored.Value;
            }
            if (stored.Value.Status.IsTerminal()) yield break;
            await Task.Delay(TimeSpan.FromMilliseconds(300), timeProvider, cancellationToken);
        }
    }

    public Task<IReadOnlyList<FlowRunEvent>> ListEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        repository.ListRunEventsAsync(runId, afterSequence, cancellationToken);

    private async Task<FlowVersion> ResolveVersionAsync(FlowId flowId, string? version, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(version))
            return (await repository.GetVersionAsync(flowId, version, cancellationToken))?.Value
                ?? throw new FlowValidationException("flow_version_not_found", $"Published Flow version '{version}' was not found.");
        var definition = await repository.GetAsync(flowId, cancellationToken) ?? throw new FlowNotFoundException(flowId);
        if (definition.Value.ActiveVersion is null)
            throw new FlowValidationException("flow_active_version_required", "A Flow Run requires a published active version.");
        return (await repository.GetVersionAsync(flowId, definition.Value.ActiveVersion, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Published Flow version '{definition.Value.ActiveVersion}' was not found.");
    }

    private static IReadOnlyList<FlowStepRun> CreateSteps(FlowVersion version, JsonElement input)
    {
        if (version.Graph is not null) return version.Graph.Steps.Select(step => new FlowStepRun { StepName = step.Name, StepType = step.Type(), DeclaredInput = step is InputFlowStepDefinition ? input.Clone() : StepDeclaredInput(step) }).ToArray();
        if (version.Definition is OrchestrationFlowDefinition orchestration)
        {
            return
            [
                new FlowStepRun { StepName = "Input", StepType = "input", DeclaredInput = input.Clone() },
                .. orchestration.Participants.Select(participant => new FlowStepRun
                {
                    StepName = participant.Id,
                    StepType = "agent",
                    DeclaredInput = input.Clone(),
                    AgentResourceId = participant.Id
                }),
                new FlowStepRun { StepName = "Output", StepType = "output" }
            ];
        }
        var steps = new List<FlowStepRun> { new() { StepName = "Input", StepType = "input", DeclaredInput = input.Clone() } };
        if (version.Definition is RoutingFlowDefinition) steps.Add(new() { StepName = "Router", StepType = "router", DeclaredInput = input.Clone() });
        steps.Add(new() { StepName = "Agent", StepType = "agent", DeclaredInput = input.Clone() });
        steps.Add(new() { StepName = "Output", StepType = "output" });
        return steps;
    }

    private async Task ExecuteOrchestrationAsync(
        StoredFlowRun initial,
        OrchestrationFlowDefinition definition,
        CancellationToken stoppingToken,
        CancellationToken runToken)
    {
        var stored = await CompleteSimpleStepAsync(initial, "Input", initial.Value.Input, null, runToken);
        var started = new HashSet<string>(StringComparer.Ordinal);
        FlowOrchestrationResult? result = null;
        var request = new FlowOrchestrationExecutionRequest(
            stored.Value.Id,
            definition,
            stored.Value.Input,
            stored.Value.CorrelationId!);

        await foreach (var executionEvent in orchestrations.ExecuteAsync(request, runToken))
        {
            switch (executionEvent)
            {
                case FlowParticipantTurnStarted turn:
                    if (started.Add(turn.ParticipantId))
                        stored = await StartStepAsync(stored, turn.ParticipantId, runToken);
                    await EmitAsync(stored.Value.Id, FlowRunEventType.ParticipantTurnStarted, turn.ParticipantId,
                        JsonSerializer.SerializeToElement(new { turn = turn.Turn }), runToken);
                    break;
                case FlowParticipantDelta delta:
                    if (started.Add(delta.ParticipantId))
                        stored = await StartStepAsync(stored, delta.ParticipantId, runToken);
                    await EmitAsync(stored.Value.Id, FlowRunEventType.StepOutputDelta, delta.ParticipantId,
                        JsonSerializer.SerializeToElement(new { content = delta.Content }), runToken);
                    break;
                case FlowParticipantTurnCompleted turn:
                    await EmitAsync(stored.Value.Id, FlowRunEventType.ParticipantTurnCompleted, turn.ParticipantId,
                        JsonSerializer.SerializeToElement(new { turn = turn.Turn }), runToken);
                    break;
                case FlowParticipantCompleted completed:
                    if (started.Add(completed.Result.ParticipantId))
                        stored = await StartStepAsync(stored, completed.Result.ParticipantId, runToken);
                    stored = await FinishParticipantStepAsync(stored, completed.Result, runToken);
                    break;
                case FlowExecutionCompleted completed:
                    result = completed.Result;
                    break;
            }
        }

        if (result is null)
            throw new FlowValidationException("flow_orchestration_output_missing", "The orchestration completed without a final output.");
        var output = JsonSerializer.SerializeToElement(result, JsonOptions);

        var now = timeProvider.GetUtcNow();
        var skipped = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.NotStarted && step.StepType == "agent"
            ? step with { Status = FlowStepRunStatus.Skipped, CompletedAt = now }
            : step).ToArray();
        stored = await SaveAsync(stored, stored.Value with { Steps = skipped }, runToken);
        stored = await CompleteSimpleStepAsync(stored, "Output", output, null, runToken);
        await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.Succeeded,
            Output = output.Clone(),
            CompletedAt = now
        }, stoppingToken);
        RecordCompletion(stored.Value.CreatedAt, now, stored.Value.DefinitionState);
        await EmitAsync(stored.Value.Id, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
    }

    private async Task<StoredFlowRun> FinishParticipantStepAsync(
        StoredFlowRun stored,
        FlowParticipantResult result,
        CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == result.ParticipantId ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = result.Output.Clone(),
            CompletedAt = now,
            AgentResourceId = result.AgentResourceId,
            AgentVersion = result.AgentVersion,
            ModelProfileResourceId = result.ModelProfileResourceId,
            Provider = result.Provider,
            Tools = result.Tools,
            Usage = result.Usage,
            Logs = [.. step.Logs, $"{result.ParticipantId} completed after {result.Turns.Count} turn(s)."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunCompleted, result.ParticipantId,
            JsonSerializer.SerializeToElement(new { turns = result.Turns.Count }), token);
        return updated;
    }

    private async Task ExecuteGraphAsync(StoredFlowRun initial, CancellationToken stoppingToken, CancellationToken runToken)
    {
        var stored = initial;
        var graph = stored.Value.DefinitionSnapshot.Graph!;
        var outputs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var currentName = graph.EntryStep;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? finalOutput = null;
        for (var count = 0; count < graph.Steps.Count; count++)
        {
            runToken.ThrowIfCancellationRequested();
            var step = graph.Steps.Single(item => item.Name == currentName);
            if (!executed.Add(step.Name)) throw new FlowValidationException("flow_cycle_detected", $"Step '{step.Name}' was reached more than once.");
            stored = await StartStepAsync(stored, step.Name, runToken);
            var context = new FlowExecutionContext(stored.Value.Input, outputs);
            JsonElement? output;
            string eventName;
            FlowAgentExecutionResult? agentResult = null;
            FlowRunError? stepError = null;
            switch (step)
            {
                case InputFlowStepDefinition:
                    output = stored.Value.Input.Clone(); eventName = "completed"; break;
                case RouterFlowStepDefinition router:
                    var selection = SelectRoute(router, stored.Value.Input);
                    output = selection is null ? null : JsonSerializer.SerializeToElement(new { selectedRoute = selection.Value.Route, selectedAgent = selection.Value.Agent.ResourceId, confidence = selection.Value.Confidence, reason = selection.Value.Reason });
                    eventName = selection is null ? "failed" : "selected";
                    if (selection is null) stepError = new FlowRunError("router_no_route", "The Router could not select a route and has no fallback.");
                    break;
                case AgentFlowStepDefinition agent:
                    var agentId = await ResolveStringAsync(agent.Agent.ResourceId, context, runToken) ?? throw new FlowValidationException("agent_reference_unresolved", $"Agent reference for step '{step.Name}' could not be resolved.");
                    var resolvedInput = agent.InputMapping is null ? stored.Value.Input.Clone() : await ResolveJsonAsync(agent.InputMapping.Value, context, runToken);
                    try
                    {
                        agentResult = await agents.ExecuteAsync(new FlowTargetReference(FlowTargetKind.Agent, agentId), resolvedInput, stored.Value.CorrelationId!, runToken);
                        output = agentResult.Output.Clone(); eventName = "completed";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message }); eventName = "failed";
                        stepError = new FlowRunError("agent_step_failed", "The Agent step failed.", exception.Message);
                    }
                    break;
                case ConditionFlowStepDefinition condition:
                    var conditionResult = await EvaluateConditionAsync(condition, context, runToken);
                    output = JsonSerializer.SerializeToElement(conditionResult); eventName = conditionResult ? "true" : "false"; break;
                case TransformFlowStepDefinition transform:
                    output = transform.Mode.Equals("Expression", StringComparison.OrdinalIgnoreCase)
                        ? await EvaluateExpressionAsync(transform.Expression!, context, runToken)
                        : transform.Mapping is null ? JsonSerializer.SerializeToElement(new { }) : await ResolveJsonAsync(transform.Mapping.Value, context, runToken);
                    eventName = "completed"; break;
                case OutputFlowStepDefinition terminal:
                    output = terminal.OutputMapping is null ? outputs.Values.LastOrDefault(value => value is not null)?.Clone() : await ResolveJsonAsync(terminal.OutputMapping.Value, context, runToken);
                    finalOutput = output; eventName = "completed"; break;
                case FailureFlowStepDefinition failure:
                    throw new FlowValidationException(failure.Code, failure.Message);
                default:
                    throw new FlowValidationException("flow_step_type_unsupported", $"Step '{step.Name}' has an unsupported type.");
            }
            outputs[step.Name] = output?.Clone();
            var transition = await SelectTransitionAsync(graph, step.Name, eventName, new FlowExecutionContext(stored.Value.Input, outputs), runToken);
            if (stepError is not null) stored = await FinishFailedStepAsync(stored, step.Name, output, transition?.Id, stepError, runToken);
            else if (agentResult is not null) stored = await FinishAgentStepAsync(stored, agentResult, runToken, transition?.Id, step.Name);
            else stored = await FinishGraphStepAsync(stored, step.Name, output, transition?.Id, runToken);
            if (step is OutputFlowStepDefinition) break;
            if (transition is null) throw new FlowValidationException("flow_transition_missing", $"No '{eventName}' transition leaves step '{step.Name}'.");
            currentName = transition.ToStep;
        }
        if (finalOutput is null) throw new FlowValidationException("flow_output_missing", "The Flow completed without reaching an Output step.");
        var now = timeProvider.GetUtcNow();
        var finalSteps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.NotStarted ? step with { Status = FlowStepRunStatus.Skipped, CompletedAt = now } : step).ToArray();
        await SaveAsync(stored, stored.Value with { Status = FlowRunStatus.Succeeded, Output = finalOutput.Value.Clone(), CompletedAt = now, Steps = finalSteps }, stoppingToken);
        RecordCompletion(stored.Value.CreatedAt, now, stored.Value.DefinitionState);
        await EmitAsync(stored.Value.Id, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
    }

    private async Task<StoredFlowRun> FinishGraphStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with { Status = FlowStepRunStatus.Succeeded, ResolvedInput = stored.Value.Input.Clone(), Output = output?.Clone(), SelectedTransition = transition, CompletedAt = now, Logs = [.. step.Logs, $"{name} completed."] } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunCompleted, name, JsonSerializer.SerializeToElement(new { transition }), token);
        return updated;
    }

    private async Task<StoredFlowRun> FinishFailedStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition, FlowRunError error, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with { Status = FlowStepRunStatus.Failed, ResolvedInput = stored.Value.Input.Clone(), Output = output?.Clone(), SelectedTransition = transition, CompletedAt = now, Error = error, Logs = [.. step.Logs, $"{name} failed: {error.Message}"] } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunFailed, name, JsonSerializer.SerializeToElement(new { transition, error.Code, error.Message }), token);
        return updated;
    }

    private async Task<FlowTransitionDefinition?> SelectTransitionAsync(FlowGraphDefinition graph, string from, string eventName, FlowExecutionContext context, CancellationToken token)
    {
        foreach (var transition in graph.Transitions.Where(item => item.FromStep == from && item.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Priority ?? int.MaxValue))
        {
            if (transition.Condition is null) return transition;
            var evaluated = await EvaluateExpressionAsync(transition.Condition, context, token);
            if (evaluated?.ValueKind == JsonValueKind.True) return transition;
        }
        return null;
    }

    private async Task<bool> EvaluateConditionAsync(ConditionFlowStepDefinition condition, FlowExecutionContext context, CancellationToken token)
    {
        if (condition.Mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase)) return (await EvaluateExpressionAsync(condition.Expression!, context, token))?.ValueKind == JsonValueKind.True;
        var left = condition.Left?.StartsWith("${", StringComparison.Ordinal) == true ? await EvaluateExpressionAsync(condition.Left, context, token) : JsonSerializer.SerializeToElement(condition.Left);
        var right = condition.Right;
        var leftText = left?.ToString() ?? string.Empty;
        return condition.Operator.ToLowerInvariant() switch
        {
            "equals" => string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "not equals" => !string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "contains" => leftText.Contains(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "starts with" => leftText.StartsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "ends with" => leftText.EndsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "greater than" => CompareCondition(leftText, right) > 0,
            "greater than or equal" => CompareCondition(leftText, right) >= 0,
            "less than" => CompareCondition(leftText, right) < 0,
            "less than or equal" => CompareCondition(leftText, right) <= 0,
            "is empty" => string.IsNullOrEmpty(leftText),
            "is not empty" => !string.IsNullOrEmpty(leftText),
            _ => throw new FlowValidationException("condition_operator_unsupported", $"Condition operator '{condition.Operator}' is not supported.")
        };
    }

    private static int CompareCondition(string left, string? right)
    {
        if (decimal.TryParse(left, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement?> EvaluateExpressionAsync(string source, FlowExecutionContext context, CancellationToken token)
    {
        var parsed = expressionParser.Parse(source);
        if (!parsed.IsValid) throw new FlowValidationException("expression_invalid", parsed.Error!);
        return await expressions.EvaluateAsync(parsed.Expression!, context, token);
    }

    private async Task<string?> ResolveStringAsync(string source, FlowExecutionContext context, CancellationToken token) => source.StartsWith("${", StringComparison.Ordinal)
        ? (await EvaluateExpressionAsync(source, context, token))?.ToString()
        : source;

    private async Task<JsonElement> ResolveJsonAsync(JsonElement value, FlowExecutionContext context, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}')) return (await EvaluateExpressionAsync(text, context, token))?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
            return value.Clone();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) result[property.Name] = await ResolveJsonAsync(property.Value, context, token);
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<JsonElement>(); foreach (var item in value.EnumerateArray()) result.Add(await ResolveJsonAsync(item, context, token)); return JsonSerializer.SerializeToElement(result);
        }
        return value.Clone();
    }

    private static (string Route, FlowResourceReference Agent, double Confidence, string Reason)? SelectRoute(RouterFlowStepDefinition router, JsonElement input)
    {
        var text = input.GetRawText();
        foreach (var candidate in router.Candidates)
            if (text.Contains(candidate.Route, StringComparison.OrdinalIgnoreCase) || (candidate.Examples?.Any(example => text.Contains(example, StringComparison.OrdinalIgnoreCase)) ?? false)) return (candidate.Route, candidate.Agent, 1, $"Input matched route '{candidate.Route}'.");
        return router.Fallback is null ? null : ("fallback", router.Fallback, .5, "No rule matched; explicit fallback selected.");
    }

    private static JsonElement? StepDeclaredInput(FlowStepDefinition step) => step switch { AgentFlowStepDefinition agent => agent.InputMapping?.Clone(), TransformFlowStepDefinition transform => transform.Mapping?.Clone(), OutputFlowStepDefinition output => output.OutputMapping?.Clone(), _ => null };
    private static void ValidateInput(JsonElement? schema, JsonElement input)
    {
        if (schema is null || schema.Value.ValueKind != JsonValueKind.Object) return;
        if (schema.Value.TryGetProperty("type", out var rootType) && rootType.GetString() == "object" && input.ValueKind != JsonValueKind.Object)
            throw new FlowValidationException("flow_input_invalid", "Flow input must be a JSON object.");
        if (input.ValueKind != JsonValueKind.Object) return;
        if (schema.Value.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in required.EnumerateArray().Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)))
                if (!input.TryGetProperty(property!, out _)) throw new FlowValidationException("flow_input_required", $"Flow input property '{property}' is required.");
        }
        if (!schema.Value.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;
        foreach (var propertySchema in properties.EnumerateObject())
        {
            if (!input.TryGetProperty(propertySchema.Name, out var value) || !propertySchema.Value.TryGetProperty("type", out var type)) continue;
            var valid = type.GetString() switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                _ => true
            };
            if (!valid) throw new FlowValidationException("flow_input_type_invalid", $"Flow input property '{propertySchema.Name}' has an invalid type.");
        }
    }

    private static void RecordCompletion(DateTimeOffset createdAt, DateTimeOffset completedAt, FlowDefinitionState definitionState)
    {
        var tag = new KeyValuePair<string, object?>("flow.definition.state", definitionState.ToString());
        RunsCompleted.Add(1, tag);
        RunDuration.Record(Math.Max(0, (completedAt - createdAt).TotalSeconds), tag);
    }

    private static FlowTargetReference SelectTarget(FlowDefinition definition, JsonElement input) => definition switch
    {
        DirectFlowDefinition direct => direct.Target,
        RoutingFlowDefinition routing => routing.Destinations.FirstOrDefault(destination => InputContains(input, destination.Id))
            ?? routing.Fallback ?? routing.Destinations[0],
        _ => throw new FlowValidationException("flow_run_kind_unsupported", "This first execution increment supports Direct and Routing Flows.")
    };

    private static bool InputContains(JsonElement input, string targetId) =>
        input.GetRawText().Contains(targetId, StringComparison.OrdinalIgnoreCase)
        || targetId.Split('-', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length > 2 && input.GetRawText().Contains(part, StringComparison.OrdinalIgnoreCase));

    private async Task<StoredFlowRun> CompleteSimpleStepAsync(StoredFlowRun stored, string name, JsonElement output, string? transition, CancellationToken token)
    {
        stored = await StartStepAsync(stored, name, token);
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = output.Clone(),
            SelectedTransition = transition,
            CompletedAt = now,
            Logs = [.. step.Logs, $"{name} completed."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunCompleted, name, JsonSerializer.SerializeToElement(new { transition }), token);
        return updated;
    }

    private async Task<StoredFlowRun> StartStepAsync(StoredFlowRun stored, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Running,
            StartedAt = now,
            Attempt = step.Attempt + 1,
            Logs = [.. step.Logs, $"{name} started."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunStarted, name, null, token);
        return updated;
    }

    private async Task<StoredFlowRun> FinishAgentStepAsync(StoredFlowRun stored, FlowAgentExecutionResult execution, CancellationToken token, string? selectedTransition = null, string stepName = "Agent")
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == stepName ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = execution.Output.Clone(),
            SelectedTransition = selectedTransition,
            CompletedAt = now,
            AgentResourceId = execution.AgentResourceId,
            AgentVersion = execution.AgentVersion,
            ModelProfileResourceId = execution.ModelProfileResourceId,
            Provider = execution.Provider,
            Usage = execution.Usage,
            Tools = execution.Tools,
            Logs = [.. step.Logs, .. execution.Logs, "Agent completed."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.Id, FlowRunEventType.StepRunCompleted, stepName, JsonSerializer.SerializeToElement(new { selectedTransition }), token);
        return updated;
    }

    private async Task FailAsync(string runId, FlowRunStatus status, string code, string message, CancellationToken token, string? details = null)
    {
        var stored = await RequiredAsync(runId, token);
        if (stored.Value.Status.IsTerminal()) return;
        var now = timeProvider.GetUtcNow();
        var error = new FlowRunError(code, message, details);
        var steps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.Running
            ? step with { Status = status == FlowRunStatus.Cancelled ? FlowStepRunStatus.Cancelled : FlowStepRunStatus.Failed, CompletedAt = now, Error = error }
            : step).ToArray();
        await SaveAsync(stored, stored.Value with { Status = status, CompletedAt = now, Error = error, Steps = steps }, token);
        if (status is FlowRunStatus.Failed or FlowRunStatus.TimedOut)
            RunsFailed.Add(1, new KeyValuePair<string, object?>("flow.definition.state", stored.Value.DefinitionState.ToString()));
        RunDuration.Record(Math.Max(0, (now - stored.Value.CreatedAt).TotalSeconds), new KeyValuePair<string, object?>("flow.status", status.ToString()));
        var eventType = status switch
        {
            FlowRunStatus.Cancelled => FlowRunEventType.FlowRunCancelled,
            FlowRunStatus.TimedOut => FlowRunEventType.FlowRunTimedOut,
            _ => FlowRunEventType.FlowRunFailed
        };
        await EmitAsync(runId, eventType,
            steps.FirstOrDefault(step => step.Status == FlowStepRunStatus.Failed)?.StepName, JsonSerializer.SerializeToElement(error), token);
    }

    private Task<StoredFlowRun> SaveAsync(StoredFlowRun stored, FlowRun value, CancellationToken token) => repository.UpdateRunAsync(value, stored.ETag, token);
    private async Task<StoredFlowRun> RequiredAsync(string id, CancellationToken token) => await repository.GetRunAsync(id, token) ?? throw new FlowRunNotFoundException(id);
    private async Task EmitAsync(string runId, FlowRunEventType type, string? stepId, JsonElement? payload, CancellationToken token)
    {
        var runEvent = await repository.AppendRunEventAsync(new FlowRunEvent(runId, 0, type, stepId, payload?.Clone(), timeProvider.GetUtcNow()), token);
        await eventSink.PublishAsync(runEvent, token);
    }
}
