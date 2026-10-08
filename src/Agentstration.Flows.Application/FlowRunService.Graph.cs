using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    private async Task ExecuteGraphAsync(StoredFlowRun initial, CancellationToken stoppingToken, CancellationToken runToken)
    {
        var stored = initial;
        var graph = stored.Value.DefinitionSnapshot.Graph!;
        var outputs = stored.Value.Steps
            .Where(step => step.Status is FlowStepRunStatus.Succeeded or FlowStepRunStatus.Failed)
            .ToDictionary(step => step.StepName, step => step.Output?.Clone(), StringComparer.Ordinal);
        var resumedChildStep = stored.Value.Steps.SingleOrDefault(step =>
            step.Status == FlowStepRunStatus.Running
            && (step.ChildFlowRunId is not null || step.ArtifactStorageFlowRunId is not null));
        var currentName = resumedChildStep?.StepName ?? graph.EntryStep;
        var incomingTransition = resumedChildStep is null
            ? null
            : SelectedIncomingTransition(graph, stored.Value, currentName);
        var executed = stored.Value.Steps
            .Where(step => step.Status is FlowStepRunStatus.Succeeded or FlowStepRunStatus.Failed)
            .Select(step => step.StepName)
            .ToHashSet(StringComparer.Ordinal);
        JsonElement? finalOutput = null;
        string? finalOutputName = null;
        FlowOutputOutcome? finalOutputOutcome = null;
        FlowRunError? finalError = null;
        for (var count = executed.Count; count < graph.Steps.Count; count++)
        {
            runToken.ThrowIfCancellationRequested();
            var step = graph.Steps.Single(item => item.Name == currentName);
            if (!executed.Add(step.Name)) throw new FlowValidationException("flow_cycle_detected", $"Step '{step.Name}' was reached more than once.");
            if (resumedChildStep?.StepName != step.Name)
                stored = await StartStepAsync(stored, step.Name, runToken);
            resumedChildStep = null;
            var transitionOutput = incomingTransition is null
                ? null
                : outputs.GetValueOrDefault(incomingTransition.FromStep)?.Clone();
            var context = ExecutionContext(stored.Value, step.Name, outputs, transitionOutput,
                stepDisplayName: step.DisplayName);
            JsonElement? output;
            string eventName;
            FlowAgentExecutionResult? agentResult = null;
            FlowToolExecutionResult? toolResult = null;
            FlowToolRouteResolution? toolRouteResolution = null;
            FlowRun? childResult = null;
            FlowRunError? stepError = null;
            IReadOnlyList<FlowStepArtifactReference> artifacts = [];
            var resumedArtifactStorage = stored.Value.Steps.Single(item => item.StepName == step.Name)
                .ArtifactStorageFlowRunId is not null;
            if (resumedArtifactStorage)
            {
                var stepRun = stored.Value.Steps.Single(item => item.StepName == step.Name);
                var storageRunId = stepRun.ArtifactStorageFlowRunId!;
                var storageCall = ArtifactStorageCall(step);
                var storageRun = await repository.GetRunAsync(stored.Value.WorkspaceId, storageRunId, runToken);
                if (storageRun is null)
                {
                    await WaitForArtifactStorageAsync(stored, step.Name, runToken);
                    await EnsureChildFlowRunAsync(stored.Value, storageCall,
                        ArtifactStorageInput(stored.Value, step.Name, stepRun.Artifacts.Single()),
                        storageRunId, runToken);
                    return;
                }
                ValidateChildIdentity(stored.Value, storageCall, storageRun.Value, storageRunId);
                output = stepRun.Output?.Clone();
                if (!storageRun.Value.Status.IsTerminal())
                {
                    await WaitForArtifactStorageAsync(stored, step.Name, runToken);
                    return;
                }
                eventName = storageRun.Value.Status switch
                {
                    FlowRunStatus.Succeeded => "completed",
                    FlowRunStatus.Failed => "failed",
                    FlowRunStatus.TimedOut => "timedOut",
                    FlowRunStatus.Cancelled => "cancelled",
                    _ => throw new InvalidOperationException(
                        $"Artifact storage Flow Run '{storageRunId}' has unsupported terminal status '{storageRun.Value.Status}'.")
                };
                if (storageRun.Value.Status == FlowRunStatus.Succeeded)
                {
                    var staged = stepRun.Artifacts.Single();
                    var durableId = DurableArtifactId(storageRun.Value.Output, storageRunId);
                    artifacts = [new(durableId, staged.FileName, staged.MediaType, "durable", storageRunId,
                        staged.LocalArtifactId ?? staged.ArtifactId)];
                }
                else
                {
                    var storageError = storageRun.Value.Error;
                    stepError = new FlowRunError(
                        storageError?.Code ?? $"artifact_storage_flow_{eventName}",
                        $"Artifact storage Flow Run '{storageRunId}' {eventName}.",
                        storageError?.Details ?? storageError?.Message);
                    artifacts = stepRun.Artifacts;
                }
            }
            else switch (step)
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
                    var resolvedInput = agent.InputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(agent.InputMapping.Value, context, runToken);
                    try
                    {
                        agentResult = await agents.ExecuteAsync(new FlowAgentExecutionRequest(
                            stored.Value.Scope,
                            stored.Value.Id,
                            step.Name,
                            new FlowTargetReference(FlowTargetKind.Agent, agentId, Namespace: agent.Agent.Namespace ?? stored.Value.FlowId.Namespace),
                            resolvedInput,
                            stored.Value.CorrelationId!), runToken);
                        output = agentResult.Output.Clone(); eventName = "success";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message }); eventName = "error";
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
                case ToolFlowStepDefinition tool:
                    var arguments = tool.ArgumentsMapping is null
                        ? JsonSerializer.SerializeToElement(new { })
                        : await ResolveJsonAsync(tool.ArgumentsMapping.Value, context, runToken);
                    try
                    {
                        toolResult = await toolExecutor.ExecuteAsync(new FlowToolExecutionRequest(
                            stored.Value.Scope,
                            stored.Value.Id,
                            stored.Value.FlowId,
                            step.Name,
                            stored.Value.Steps.Single(item => item.StepName == step.Name).Attempt,
                            stored.Value.CorrelationId!,
                            tool.Tool,
                            arguments), runToken);
                        output = toolResult.Output?.Clone();
                        eventName = "success";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message });
                        eventName = "error";
                        stepError = exception is FlowValidationException validation
                            ? new FlowRunError(validation.Code, "The Tool step failed.", validation.Message)
                            : new FlowRunError("tool_step_failed", "The Tool step failed.", exception.Message);
                    }
                    break;
                case ToolRouteFlowStepDefinition route:
                    var routeArguments = route.ArgumentsMapping is null
                        ? JsonSerializer.SerializeToElement(new { })
                        : await ResolveJsonAsync(route.ArgumentsMapping.Value, context, runToken);
                    try
                    {
                        var resolvedRoute = await toolSetResolver.ResolveAsync(stored.Value.WorkspaceId,
                            stored.Value.FlowId.Namespace, route, runToken);
                        toolRouteResolution = new(
                            resolvedRoute.ToolSetName,
                            resolvedRoute.ToolSetNamespace,
                            resolvedRoute.ToolSetVersion,
                            resolvedRoute.Capability,
                            resolvedRoute.Route,
                            resolvedRoute.Tool.ResourceId,
                            resolvedRoute.Tool.ResolveNamespace(stored.Value.FlowId.Namespace),
                            resolvedRoute.ToolUid,
                            resolvedRoute.ToolGeneration,
                            resolvedRoute.ProviderName,
                            resolvedRoute.ProviderNamespace);
                        toolResult = await toolExecutor.ExecuteAsync(new FlowToolExecutionRequest(
                            stored.Value.Scope,
                            stored.Value.Id,
                            stored.Value.FlowId,
                            step.Name,
                            stored.Value.Steps.Single(item => item.StepName == step.Name).Attempt,
                            stored.Value.CorrelationId!,
                            resolvedRoute.Tool,
                            routeArguments), runToken);
                        output = toolResult.Output?.Clone();
                        eventName = "completed";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message });
                        eventName = "failed";
                        stepError = exception is FlowValidationException validation
                            ? new FlowRunError(validation.Code, "The ToolRoute step failed.", validation.Message)
                            : new FlowRunError("tool_route_step_failed", "The ToolRoute step failed.", exception.Message);
                    }
                    break;
                case FlowCallStepDefinition flowCall:
                    var callInput = flowCall.InputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(flowCall.InputMapping.Value, context, runToken);
                    var stepRun = stored.Value.Steps.Single(item => item.StepName == step.Name);
                    var childRunId = stepRun.ChildFlowRunId ?? ChildFlowRunId(stored.Value, step.Name, stepRun.Attempt);
                    var child = await repository.GetRunAsync(stored.Value.WorkspaceId, childRunId, runToken);
                    if (child is null)
                    {
                        stored = await SuspendForChildAsync(stored, step.Name, childRunId, runToken, callInput);
                        await EnsureChildFlowRunAsync(stored.Value, flowCall, callInput, childRunId, runToken);
                        return;
                    }
                    ValidateChildIdentity(stored.Value, flowCall, child.Value, childRunId);
                    if (!child.Value.Status.IsTerminal())
                    {
                        await SuspendForChildAsync(stored, step.Name, childRunId, runToken);
                        return;
                    }
                    output = child.Value.Output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
                    childResult = child.Value;
                    eventName = child.Value.OutputName ?? child.Value.Status switch
                    childResult = child.Value;
                    {
                        FlowRunStatus.Succeeded => "completed",
                        FlowRunStatus.Failed => "failed",
                        FlowRunStatus.TimedOut => "timedOut",
                        FlowRunStatus.Cancelled => "cancelled",
                        _ => throw new InvalidOperationException($"Child Flow Run '{childRunId}' has unsupported terminal status '{child.Value.Status}'.")
                    };
                    if (child.Value.Status != FlowRunStatus.Succeeded)
                    {
                        var childError = child.Value.Error;
                        stepError = new FlowRunError(
                            childError?.Code ?? $"child_flow_{eventName}",
                            $"Child Flow Run '{childRunId}' {eventName}.",
                            childError?.Details ?? childError?.Message);
                    }
                    break;
                case RepeatFlowStepDefinition repeat:
                    var repeatStepRun = stored.Value.Steps.Single(item => item.StepName == step.Name);
                    var iteration = repeatStepRun.RepeatIteration ?? 1;
                    var repeatInput = repeatStepRun.ResolvedInput?.Clone()
                        ?? (repeat.InputMapping is null
                            ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                            : await ResolveJsonAsync(repeat.InputMapping.Value, context, runToken));
                    var repeatChildRunId = repeatStepRun.ChildFlowRunId
                        ?? ChildFlowRunId(stored.Value, step.Name, repeatStepRun.Attempt, iteration);
                    var repeatCall = RepeatCall(repeat);
                    var repeatChild = await repository.GetRunAsync(stored.Value.WorkspaceId, repeatChildRunId, runToken);
                    if (repeatChild is null)
                    {
                        stored = await SuspendForChildAsync(stored, step.Name, repeatChildRunId, runToken, repeatInput, iteration);
                        await EnsureChildFlowRunAsync(stored.Value, repeatCall, repeatInput, repeatChildRunId, runToken);
                        return;
                    }
                    ValidateChildIdentity(stored.Value, repeatCall, repeatChild.Value, repeatChildRunId);
                    if (!repeatChild.Value.Status.IsTerminal())
                    {
                        await SuspendForChildAsync(stored, step.Name, repeatChildRunId, runToken, repeatInput, iteration);
                        return;
                    }
                    output = repeatChild.Value.Output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
                    childResult = repeatChild.Value;
                    eventName = repeatChild.Value.Status switch
                    {
                        FlowRunStatus.Succeeded => "completed",
                        FlowRunStatus.Failed => "failed",
                        FlowRunStatus.TimedOut => "timedOut",
                        FlowRunStatus.Cancelled => "cancelled",
                        _ => throw new InvalidOperationException($"Child Flow Run '{repeatChildRunId}' has unsupported terminal status '{repeatChild.Value.Status}'.")
                    };
                    if (repeatChild.Value.Status != FlowRunStatus.Succeeded)
                    {
                        var childError = repeatChild.Value.Error;
                        stepError = new FlowRunError(
                            childError?.Code ?? $"child_flow_{eventName}",
                            $"Child Flow Run '{repeatChildRunId}' {eventName}.",
                            childError?.Details ?? childError?.Message);
                        break;
                    }

                    outputs[step.Name] = output.Value.Clone();
                    var until = await EvaluateExpressionAsync(
                        repeat.Until,
                        ExecutionContext(stored.Value, step.Name, outputs, output,
                            stepDisplayName: step.DisplayName),
                        runToken);
                    if (until?.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        throw new FlowValidationException("flow_repeat_until_invalid", $"Repeat step '{step.Name}' until expression must return a boolean.");
                    if (until.Value.ValueKind == JsonValueKind.True) break;
                    if (iteration >= repeat.MaximumIterations)
                        throw new FlowValidationException("flow_repeat_limit_exceeded", $"Repeat step '{step.Name}' reached its maximum of {repeat.MaximumIterations} iterations.");

                    var nextInput = repeat.NextInputMapping is null
                        ? output.Value.Clone()
                        : await ResolveJsonAsync(
                            repeat.NextInputMapping.Value,
                            ExecutionContext(stored.Value, step.Name, outputs, output,
                                stepDisplayName: step.DisplayName),
                            runToken);
                    var nextIteration = iteration + 1;
                    var nextChildRunId = ChildFlowRunId(stored.Value, step.Name, repeatStepRun.Attempt, nextIteration);
                    stored = await SuspendForChildAsync(stored, step.Name, nextChildRunId, runToken, nextInput, nextIteration);
                    await EnsureChildFlowRunAsync(stored.Value, repeatCall, nextInput, nextChildRunId, runToken);
                    return;
                case OutputFlowStepDefinition terminal:
                    output = terminal.OutputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(terminal.OutputMapping.Value, context, runToken);
                    finalOutput = output;
                    finalOutputName = terminal.Outcome.HasValue ? terminal.Name : null;
                    finalOutputOutcome = terminal.Outcome ?? FlowOutputOutcome.Success;
                    eventName = terminal.Outcome.HasValue ? terminal.Name : "completed";
                    if (terminal.Outcome == FlowOutputOutcome.Error)
                    {
                        var details = terminal.DetailsExpression is null
                            ? null
                            : await ResolveStringAsync(terminal.DetailsExpression, context, runToken);
                        stepError = finalError = new FlowRunError(
                            terminal.Code ?? "FLOW_FAILED",
                            terminal.Message ?? "Flow execution failed.",
                            details);
                    }
                    break;
                case FailureFlowStepDefinition failure:
                    output = JsonSerializer.SerializeToElement(new { error = failure.Message, code = failure.Code });
                    finalOutput = output;
                    finalOutputName = null;
                    finalOutputOutcome = FlowOutputOutcome.Error;
                    eventName = failure.Name;
                    stepError = finalError = new FlowRunError(failure.Code, failure.Message, failure.DetailsExpression);
                    break;
                default:
                    throw new FlowValidationException("flow_step_type_unsupported", $"Step '{step.Name}' has an unsupported type.");
            }
            outputs[step.Name] = output?.Clone();
            if (!resumedArtifactStorage && stepError is null && step.ArtifactOutput is not null)
            {
                var artifactContext = ExecutionContext(stored.Value, step.Name, outputs,
                    currentStepOutput: output, stepDisplayName: step.DisplayName);
                var artifactDefinition = await ResolveArtifactDefinitionAsync(
                    step.ArtifactOutput, artifactContext, runToken);
                var content = await ResolveArtifactContentAsync(artifactDefinition, output,
                    artifactContext, runToken);
                var stepRun = stored.Value.Steps.Single(item => item.StepName == step.Name);
                var captured = await artifactCapture.CaptureAsync(new(
                    stored.Value.Scope,
                    stored.Value.Id,
                    stored.Value.RootFlowRunId ?? stored.Value.Id,
                    stored.Value.ParentFlowRunId,
                    step.Name,
                    stepRun.Attempt,
                    stored.Value.CorrelationId,
                    artifactDefinition,
                    content,
                    ArtifactProvenance(stored.Value, step, stepRun.Attempt, agentResult, toolResult,
                        toolRouteResolution, childResult)), runToken);
                artifacts = [captured];
                if (artifactDefinition.StorageFlow is not null)
                {
                    var storageRunId = ChildFlowRunId(stored.Value,
                        $"{step.Name}:artifact-storage", stepRun.Attempt);
                    stored = await SuspendForArtifactStorageAsync(stored, step.Name, output, captured,
                        storageRunId, agentResult, toolRouteResolution, runToken);
                    await EnsureChildFlowRunAsync(stored.Value, ArtifactStorageCall(step),
                        ArtifactStorageInput(stored.Value, step.Name, captured), storageRunId, runToken);
                    return;
                }
            }
            var transition = await SelectTransitionAsync(graph, step.Name, eventName,
                ExecutionContext(stored.Value, step.Name, outputs,
                    stepDisplayName: step.DisplayName), runToken);
            if (stepError is not null) stored = await FinishFailedStepAsync(stored, step.Name, output, transition?.Id, stepError, runToken, toolRouteResolution);
            else if (agentResult is not null) stored = await FinishAgentStepAsync(stored, agentResult, runToken, transition?.Id, step.Name, artifacts);
            else if (toolRouteResolution is not null) stored = await FinishToolRouteStepAsync(stored, step.Name, output, transition?.Id, toolRouteResolution, runToken, artifacts);
            else stored = await FinishGraphStepAsync(stored, step.Name, output, transition?.Id, runToken, artifacts);
            if (step is OutputFlowStepDefinition or FailureFlowStepDefinition) break;
            if (transition is null && stepError is not null)
                throw new FlowValidationException(stepError.Code, stepError.Details ?? stepError.Message);
            if (transition is null) throw new FlowValidationException("flow_transition_missing", $"No '{eventName}' transition leaves step '{step.Name}'.");
            incomingTransition = transition;
            currentName = transition.ToStep;
        }
        if (finalOutput is null) throw new FlowValidationException("flow_output_missing", "The Flow completed without reaching an Output step.");
        await CleanupStepArtifactsAsync(stored.Value, graph, stoppingToken);
        var now = timeProvider.GetUtcNow();
        var finalSteps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.NotStarted ? step with { Status = FlowStepRunStatus.Skipped, CompletedAt = now } : step).ToArray();
        await SaveAsync(stored, stored.Value with
        {
            Status = finalOutputOutcome == FlowOutputOutcome.Error ? FlowRunStatus.Failed : FlowRunStatus.Succeeded,
            Output = finalOutput.Value.Clone(),
            OutputName = finalOutputName,
            OutputOutcome = finalOutputOutcome,
            Error = finalError,
            CompletedAt = now,
            Steps = finalSteps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, stoppingToken);
        if (finalOutputOutcome == FlowOutputOutcome.Error)
        {
            RunsFailed.Add(1, new KeyValuePair<string, object?>("flow.definition.state", stored.Value.DefinitionState.ToString()));
            RunDuration.Record(Math.Max(0, (now - stored.Value.CreatedAt).TotalSeconds), new KeyValuePair<string, object?>("flow.status", FlowRunStatus.Failed.ToString()));
        }
        else
        {
            RecordCompletion(stored.Value.CreatedAt, now, stored.Value.DefinitionState);
        }
        await EmitAsync(
            stored.Value.WorkspaceId,
            stored.Value.Id,
            finalOutputOutcome == FlowOutputOutcome.Error ? FlowRunEventType.FlowRunFailed : FlowRunEventType.FlowRunCompleted,
            null,
            JsonSerializer.SerializeToElement(new { outputName = finalOutputName, outcome = finalOutputOutcome }),
            stoppingToken);
    }

    private async Task CleanupStepArtifactsAsync(
        FlowRun run,
        FlowGraphDefinition graph,
        CancellationToken cancellationToken)
    {
        foreach (var stepRun in run.Steps)
        {
            var definition = graph.Steps.Single(step => step.Name == stepRun.StepName).ArtifactOutput;
            if (definition is null
                || definition.Clean == FlowStepArtifactCleanupMode.Never)
                continue;

            foreach (var artifact in stepRun.Artifacts)
                await artifactCapture.CleanupAsync(run.Scope, run.Id, stepRun.StepName, artifact,
                    cancellationToken);
        }
    }

    private async Task TryCleanupStepArtifactsAsync(FlowRun run, CancellationToken cancellationToken)
    {
        if (run.DefinitionSnapshot.Graph is not { } graph) return;
        try
        {
            await CleanupStepArtifactsAsync(run, graph, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Activity.Current?.AddEvent(new ActivityEvent("flow.artifact.cleanup.failed", tags:
                new ActivityTagsCollection
                {
                    ["flow.run.id"] = run.Id,
                    ["error.type"] = exception.GetType().FullName,
                    ["error.message"] = exception.Message
                }));
        }
    }

    private static FlowExecutionContext ExecutionContext(
        FlowRun run,
        string stepName,
        IReadOnlyDictionary<string, JsonElement?> outputs,
        JsonElement? transitionOutput = null,
        JsonElement? currentStepOutput = null,
        string? stepDisplayName = null) => new(
            run.Input,
            outputs,
            transitionOutput,
            new(
                run.Id,
                run.RootFlowRunId ?? run.Id,
                run.ParentFlowRunId,
                stepName,
                run.CorrelationId,
                stepDisplayName),
            currentStepOutput);

    private async Task<StoredFlowRun> FinishToolRouteStepAsync(StoredFlowRun stored, string name,
        JsonElement? output, string? transition, FlowToolRouteResolution resolution, CancellationToken token,
        IReadOnlyList<FlowStepArtifactReference>? artifacts = null)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = output?.Clone(),
            SelectedTransition = transition,
            CompletedAt = now,
            ToolRoute = resolution,
            Tools = [CatalogId(resolution.ToolNamespace, resolution.ToolName)],
            Provider = CatalogId(resolution.ProviderNamespace, resolution.ProviderName),
            Artifacts = artifacts ?? [],
            Logs = [.. step.Logs, $"{name} resolved {resolution.ToolSetNamespace}/{resolution.ToolSetName}:{resolution.ToolSetVersion} to {resolution.ToolNamespace}/{resolution.ToolName}."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, name,
            JsonSerializer.SerializeToElement(new { transition, resolution }), token);
        return updated;
    }

    private static string CatalogId(ResourceNamespace @namespace, string name) =>
        @namespace.IsDefault ? name : $"{@namespace.Value}/{name}";

    private static FlowCallStepDefinition ArtifactStorageCall(FlowStepDefinition step) => new()
    {
        Name = $"{step.Name}:artifact-storage",
        DisplayName = $"Store Artifact from {step.DisplayName ?? step.Name}",
        Flow = step.ArtifactOutput?.StorageFlow
            ?? throw new FlowValidationException("flow_step_artifact_storage_flow_missing",
                $"Step '{step.Name}' does not declare an Artifact storage Flow.")
    };

    private static JsonElement ArtifactStorageInput(
        FlowRun run,
        string stepName,
        FlowStepArtifactReference artifact) => JsonSerializer.SerializeToElement(new
        {
            stagedArtifactId = artifact.ArtifactId,
            producerFlowRunId = run.Id,
            producerFlowStepId = stepName
        });

    private static string DurableArtifactId(JsonElement? output, string storageRunId)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("flowRunArtifactId", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new FlowValidationException("flow_step_artifact_storage_result_invalid",
                $"Artifact storage Flow Run '{storageRunId}' did not return a flowRunArtifactId.");
        return id.GetString()!;
    }

    private async Task<StoredFlowRun> SuspendForArtifactStorageAsync(
        StoredFlowRun stored,
        string stepName,
        JsonElement? output,
        FlowStepArtifactReference artifact,
        string storageRunId,
        FlowAgentExecutionResult? agent,
        FlowToolRouteResolution? route,
        CancellationToken token)
    {
        var steps = stored.Value.Steps.Select(step => step.StepName == stepName ? step with
        {
            ResolvedInput = step.ResolvedInput?.Clone() ?? stored.Value.Input.Clone(),
            Output = output?.Clone(),
            AgentResourceId = agent?.AgentResourceId ?? step.AgentResourceId,
            AgentVersion = agent?.AgentVersion ?? step.AgentVersion,
            ModelProfileResourceId = agent?.ModelProfileResourceId ?? step.ModelProfileResourceId,
            Provider = route is null ? agent?.Provider ?? step.Provider
                : CatalogId(route.ProviderNamespace, route.ProviderName),
            Usage = agent?.Usage ?? step.Usage,
            Tools = route is null ? agent?.Tools ?? step.Tools
                : [CatalogId(route.ToolNamespace, route.ToolName)],
            ToolRoute = route ?? step.ToolRoute,
            Artifacts = [artifact],
            ArtifactStorageFlowRunId = storageRunId,
            ChildFlowRunIds = step.ChildFlowRunIds.Contains(storageRunId, StringComparer.Ordinal)
                ? step.ChildFlowRunIds
                : [.. step.ChildFlowRunIds, storageRunId],
            Logs = [.. step.Logs, .. agent?.Logs ?? [], $"{stepName} captured an Artifact and started storage Flow '{storageRunId}'."]
        } : step).ToArray();
        var suspended = await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.WaitingForChild,
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, token);
        await EmitAsync(suspended.Value.WorkspaceId, suspended.Value.Id,
            FlowRunEventType.FlowRunWaitingForChild, stepName,
            JsonSerializer.SerializeToElement(new { childFlowRunId = storageRunId, artifactStorage = true }), token);
        return suspended;
    }

    private async Task WaitForArtifactStorageAsync(StoredFlowRun stored, string stepName, CancellationToken token)
    {
        var suspended = await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.WaitingForChild,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, token);
        await EmitAsync(suspended.Value.WorkspaceId, suspended.Value.Id,
            FlowRunEventType.FlowRunWaitingForChild, stepName,
            JsonSerializer.SerializeToElement(new
            {
                childFlowRunId = suspended.Value.Steps.Single(value => value.StepName == stepName)
                    .ArtifactStorageFlowRunId,
                artifactStorage = true
            }), token);
    }

    private static IReadOnlyDictionary<string, string> ArtifactProvenance(
        FlowRun run,
        FlowStepDefinition step,
        int attempt,
        FlowAgentExecutionResult? agent,
        FlowToolExecutionResult? tool,
        FlowToolRouteResolution? route,
        FlowRun? child)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invocationKind"] = step switch
            {
                AgentFlowStepDefinition => "agent",
                ToolFlowStepDefinition or ToolRouteFlowStepDefinition => "tool",
                FlowCallStepDefinition or RepeatFlowStepDefinition => "subFlow",
                OutputFlowStepDefinition => "flow",
                _ => step.Type()
            },
            ["flowName"] = run.FlowId.Value,
            ["flowNamespace"] = run.FlowId.Namespace.Value,
            ["flowVersion"] = run.FlowVersion,
            ["rootFlowRunId"] = run.RootFlowRunId ?? run.Id,
            ["stepAttempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (run.ParentFlowRunId is not null) provenance["parentFlowRunId"] = run.ParentFlowRunId;
        if (agent is not null)
        {
            provenance["agentResourceId"] = agent.AgentResourceId;
            provenance["agentVersion"] = agent.AgentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (agent.ModelProfileResourceId is not null) provenance["modelProfileResourceId"] = agent.ModelProfileResourceId;
            if (agent.Provider is not null) provenance["provider"] = agent.Provider;
        }
        if (tool is not null)
        {
            provenance["toolName"] = tool.ToolName;
            provenance["toolNamespace"] = tool.ToolNamespace.Value;
            provenance["toolUid"] = tool.ToolUid.ToString("D");
            provenance["toolGeneration"] = tool.ToolGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            provenance["toolExternalId"] = tool.ExternalToolId;
            provenance["providerName"] = tool.ProviderName;
            provenance["providerNamespace"] = tool.ProviderNamespace.Value;
            provenance["providerType"] = tool.ProviderType;
        }
        if (route is not null)
        {
            provenance["toolSetName"] = route.ToolSetName;
            provenance["toolSetNamespace"] = route.ToolSetNamespace.Value;
            provenance["toolSetVersion"] = route.ToolSetVersion;
            provenance["toolRoute"] = route.Route;
        }
        if (child is not null)
        {
            provenance["childFlowRunId"] = child.Id;
            provenance["childFlowName"] = child.FlowId.Value;
            provenance["childFlowNamespace"] = child.FlowId.Namespace.Value;
            provenance["childFlowVersion"] = child.FlowVersion;
            if (child.DefinitionHash is not null) provenance["childFlowDefinitionHash"] = child.DefinitionHash;
        }
        return provenance;
    }

    private static FlowTransitionDefinition? SelectedIncomingTransition(
        FlowGraphDefinition graph,
        FlowRun run,
        string stepName) =>
        graph.Transitions.FirstOrDefault(transition =>
            transition.ToStep == stepName
            && run.Steps.Any(step =>
                step.StepName == transition.FromStep
                && step.SelectedTransition == transition.Id));

    private async Task<StoredFlowRun> FinishGraphStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition,
        CancellationToken token, IReadOnlyList<FlowStepArtifactReference>? artifacts = null)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with { Status = FlowStepRunStatus.Succeeded, ResolvedInput = step.ResolvedInput?.Clone() ?? stored.Value.Input.Clone(), Output = output?.Clone(), SelectedTransition = transition, CompletedAt = now, Artifacts = artifacts ?? [], Logs = [.. step.Logs, $"{name} completed."] } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, name, JsonSerializer.SerializeToElement(new { transition }), token);
        return updated;
    }

    private async Task<StoredFlowRun> FinishFailedStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition, FlowRunError error, CancellationToken token, FlowToolRouteResolution? toolRoute = null)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Failed,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = output?.Clone(),
            SelectedTransition = transition,
            CompletedAt = now,
            Error = error,
            ToolRoute = toolRoute,
            Tools = toolRoute is null ? step.Tools : [CatalogId(toolRoute.ToolNamespace, toolRoute.ToolName)],
            Provider = toolRoute is null ? step.Provider : CatalogId(toolRoute.ProviderNamespace, toolRoute.ProviderName),
            Logs = [.. step.Logs, $"{name} failed: {error.Message}"]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunFailed, name, JsonSerializer.SerializeToElement(new { transition, error.Code, error.Message }), token);
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

    private async Task<JsonElement> ResolveArtifactContentAsync(
        FlowStepArtifactOutputDefinition definition,
        JsonElement? output,
        FlowExecutionContext context,
        CancellationToken token)
    {
        if (definition.ContentMapping is null)
            return output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
        var mapping = definition.ContentMapping.Value;
        if (mapping.ValueKind == JsonValueKind.String
            && mapping.GetString() is { } expression
            && expression.StartsWith("${", StringComparison.Ordinal)
            && expression.EndsWith('}'))
        {
            return (await EvaluateExpressionAsync(expression, context, token))?.Clone()
                ?? throw new FlowValidationException("flow_step_artifact_result_missing",
                    "The configured Artifact result selector did not resolve a value.");
        }
        return await ResolveJsonAsync(mapping, context, token);
    }

    private async Task<FlowStepArtifactOutputDefinition> ResolveArtifactDefinitionAsync(
        FlowStepArtifactOutputDefinition definition,
        FlowExecutionContext context,
        CancellationToken token)
    {
        var fileName = definition.FileName is null
            ? null
            : await ResolveStringAsync(definition.FileName, context, token)
                ?? throw new FlowValidationException("flow_step_artifact_file_name_unresolved",
                    "The configured Artifact file name did not resolve a value.");
        var mediaType = await ResolveStringAsync(definition.MediaType, context, token)
            ?? throw new FlowValidationException("flow_step_artifact_media_type_unresolved",
                "The configured Artifact media type did not resolve a value.");
        return definition with { FileName = fileName, MediaType = mediaType };
    }

    private static FlowCallStepDefinition RepeatCall(RepeatFlowStepDefinition repeat) => new()
    {
        Name = repeat.Name,
        DisplayName = repeat.DisplayName,
        Description = repeat.Description,
        Flow = repeat.Flow,
        InputMapping = repeat.InputMapping
    };

    private static JsonElement? StepDeclaredInput(FlowStepDefinition step) => step switch { AgentFlowStepDefinition agent => agent.InputMapping?.Clone(), FlowCallStepDefinition flow => flow.InputMapping?.Clone(), RepeatFlowStepDefinition repeat => repeat.InputMapping?.Clone(), ToolFlowStepDefinition tool => tool.ArgumentsMapping?.Clone(), ToolRouteFlowStepDefinition route => route.ArgumentsMapping?.Clone(), TransformFlowStepDefinition transform => transform.Mapping?.Clone(), OutputFlowStepDefinition output => output.OutputMapping?.Clone(), _ => null };
}

