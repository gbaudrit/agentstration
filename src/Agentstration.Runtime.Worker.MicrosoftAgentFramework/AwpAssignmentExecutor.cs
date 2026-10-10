using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpAssignmentExecutor(
    ILoggerFactory loggerFactory,
    ILogger<AwpAssignmentExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<AwpAssignmentExecutionResult> ExecuteAsync(AwpAssignmentSession session, AwpExecutionMaterial material,
        CancellationToken cancellationToken) => material switch
        {
            AwpDirectAgentExecutionMaterial direct => new(await ExecuteDirectAsync(session, direct, cancellationToken)),
            AwpRootFlowExecutionMaterial flow => await ExecuteFlowAsync(session, flow, cancellationToken),
            _ => throw new AwpExecutionNotSupportedException("material_kind_unsupported", "The execution material kind is unsupported.")
        };

    private async Task<JsonElement?> ExecuteDirectAsync(AwpAssignmentSession session,
        AwpDirectAgentExecutionMaterial material, CancellationToken cancellationToken)
    {
        var turn = (await session.Client.OpenTurnAsync(new(session.Context, material.RunId, ParticipantId: material.Agent.ParticipantId),
            Guid.NewGuid(), cancellationToken)).Turn;
        var location = new AwpExecutionLocation(material.RunId, AgentTurn: turn);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnStarted, location, null, null, cancellationToken);

        var chatClient = new AwpModelChatClient(session, material.Agent, turn);
        var toolPipeline = new AwpToolExecutionPipeline(session, material.Agent, turn);
        var factory = new AgentFrameworkRuntimeFactory(new AwpChatClientResolver(chatClient), loggerFactory,
            new GenAiObservabilityOptions { Enabled = true });
        var definition = new ExecutableAgentDefinition
        {
            AgentId = material.Agent.AgentId,
            AgentKey = material.Agent.AgentName,
            DisplayName = material.Agent.DisplayName,
            Description = material.Agent.Description,
            AgentVersion = material.Agent.Generation,
            EffectiveInstructions = material.Agent.Instructions,
            ModelProfileName = material.Agent.ModelProfileName,
            ModelProfileNamespace = ResourceNamespace.Parse(material.Agent.ModelProfileNamespace),
            RuntimeProfileName = material.Agent.RuntimeProfileName,
            RuntimeProfileNamespace = ResourceNamespace.Parse(material.Agent.RuntimeProfileNamespace),
            EffectiveToolNames = material.Agent.Tools.Select(tool => tool.Id).ToArray(),
            MiddlewareIds = [],
            ContextProviderIds = [],
            Capabilities = [],
            Handler = material.Agent.Handler,
            DefinitionHash = material.Agent.DefinitionHash
        };
        var runtime = await factory.CreateAsync(definition, material.Agent.RevisionId,
            new AgentRuntimeContext(new AwpToolCatalog(material.Agent.Tools), toolPipeline), cancellationToken);
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest(
            FormatInput(material),
            material.RunId,
            Execution: new AgentExecutionOptions { Streaming = RuntimeStreamingMode.Disabled },
            ToolExecution: new ToolExecutionScope
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                TenantId = session.Assignment.Scope.TenantId,
                WorkspaceId = ParseWorkspace(session.Assignment.Scope.WorkspaceId),
                ExecutionId = material.RunId,
                AgentGeneration = material.Agent.Generation,
                PersistArguments = material.Execution.PersistToolArguments
            }), cancellationToken);
        if (!string.IsNullOrEmpty(result.Output))
            await session.AppendEventAsync(AwpExecutionEventKind.ResponseDelta, location,
                JsonSerializer.SerializeToElement(new { content = result.Output }), null, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnCompleted, location, null, null, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.Diagnostic, location,
            JsonSerializer.SerializeToElement(new
            {
                model = result.ModelName,
                inputTokens = result.Usage?.InputTokens,
                outputTokens = result.Usage?.OutputTokens
            }), null, cancellationToken);
        return JsonSerializer.SerializeToElement(result.Output);
    }

    private async Task<AwpAssignmentExecutionResult> ExecuteFlowAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, CancellationToken cancellationToken)
    {
        var version = material.Definition.Deserialize<FlowVersion>(JsonOptions)
            ?? throw new AwpExecutionNotSupportedException("flow_material_invalid", "The assigned Flow definition is invalid.");
        if (version.Graph is not null)
            return await ExecuteGraphAsync(session, material, version.Graph, cancellationToken);
        var output = version.Definition switch
        {
            DirectFlowDefinition direct => await ExecuteSimpleAsync(session, material, direct.Target, false, cancellationToken),
            RoutingFlowDefinition routing => await ExecuteSimpleAsync(session, material,
                SelectTarget(routing, material.Input), true, cancellationToken),
            OrchestrationFlowDefinition orchestration => await ExecuteOrchestrationAsync(session, material,
                orchestration, cancellationToken),
            _ => throw new AwpExecutionNotSupportedException("flow_kind_unsupported", "The assigned Flow kind is unsupported by this Worker.")
        };
        return new(output);
    }

    private async Task<JsonElement?> ExecuteOrchestrationAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, OrchestrationFlowDefinition orchestration,
        CancellationToken cancellationToken)
    {
        if (material.Resume is null)
            await CompleteStepAsync(session, material, "Input", material.Input, null, cancellationToken);
        var coordinator = new AwpFlowExecutionCoordinator(session, material);
        var tools = material.Agents.SelectMany(value => value.Tools)
            .GroupBy(value => value.Id, StringComparer.Ordinal).Select(value => value.First()).ToArray();
        var chatClients = new AwpFlowChatClientResolver(session, material, coordinator);
        var toolExecution = new AwpFlowToolExecutionPipeline(session, coordinator);
        var factory = new AgentFrameworkRuntimeFactory(chatClients, loggerFactory,
            new GenAiObservabilityOptions { Enabled = true });
        var engine = new AgentFrameworkFlowOrchestrationEngine(
            new AwpFlowAgentResolver(material), new AwpToolCatalog(tools), factory,
            new AwpRuntimeExecutionStateStore(session, material), configuredToolExecution: toolExecution);
        var runtimeState = material.Resume is null ? null : new DurableRuntimeStateReference(
            material.Resume.RuntimeType, material.Resume.StateId, material.Resume.RespondedAt);
        var answered = material.Resume is null ? null : new InputRequest
        {
            WorkspaceId = ParseWorkspace(session.Assignment.Scope.WorkspaceId),
            Id = material.Resume.InputRequestId,
            RunId = material.RunId,
            RuntimeRequestId = material.Resume.RuntimeRequestId,
            Prompt = material.Resume.Prompt,
            Type = Enum.Parse<InputRequestType>(material.Resume.InputType),
            Options = material.Resume.Options,
            Source = material.Resume.Source,
            CreatedAt = material.Resume.RespondedAt,
            Status = InputRequestStatus.Answered,
            Response = new(material.Resume.RespondedAt, material.Resume.Response, material.Resume.PrincipalId)
        };
        JsonElement? final = null;
        await foreach (var executionEvent in engine.ExecuteAsync(new(
            ParseWorkspace(session.Assignment.Scope.WorkspaceId), material.RunId, orchestration,
            material.Input, session.Assignment.AssignmentId.Value.ToString("N"),
            RuntimeState: runtimeState, AnsweredInput: answered,
            Scope: new(session.Assignment.Scope.TenantId,
                ParseWorkspace(session.Assignment.Scope.WorkspaceId), material.PrincipalId)), cancellationToken))
        {
            switch (executionEvent)
            {
                case FlowParticipantHandoff handoff:
                    await session.AppendEventAsync(AwpExecutionEventKind.Diagnostic, new(material.RunId),
                        JsonSerializer.SerializeToElement(new { handoff.FromParticipantId, handoff.ToParticipantId }),
                        null, cancellationToken);
                    break;
                case FlowParticipantCompleted completed:
                    var step = await coordinator.StepAsync(completed.Result.ParticipantId, cancellationToken);
                    await session.AppendEventAsync(AwpExecutionEventKind.StepCompleted,
                        new(material.RunId, FlowStep: step), JsonSerializer.SerializeToElement(new
                        {
                            status = "succeeded",
                            output = completed.Result.Output,
                            turns = completed.Result.Turns.Count,
                            agentResourceId = completed.Result.AgentResourceId,
                            agentVersion = completed.Result.AgentVersion
                        }), null, cancellationToken);
                    break;
                case FlowExternalInputRequested input:
                    await session.AppendEventAsync(AwpExecutionEventKind.WaitingForInput, new(material.RunId),
                        JsonSerializer.SerializeToElement(new
                        {
                            input.RuntimeRequestId,
                            input.Prompt,
                            type = input.Type.ToString(),
                            input.Options,
                            input.Source,
                            runtimeType = input.RuntimeState.RuntimeType,
                            stateId = input.RuntimeState.StateId,
                            stateCreatedAt = input.RuntimeState.CreatedAt
                        }), null, cancellationToken);
                    throw new AwpWaitingForInputException();
                case FlowExecutionCompleted completed:
                    final = JsonSerializer.SerializeToElement(completed.Result, JsonOptions);
                    break;
            }
        }
        if (final is null)
            throw new AwpExecutionNotSupportedException("flow_orchestration_output_missing",
                "Microsoft Agent Framework completed without a final Flow output.");
        await CompleteStepAsync(session, material, "Output", final, null, cancellationToken);
        return final;
    }

    private async Task<JsonElement?> ExecuteSimpleAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, FlowTargetReference target, bool routed,
        CancellationToken cancellationToken)
    {
        await CompleteStepAsync(session, material, "Input", material.Input, null, cancellationToken);
        if (routed)
            await CompleteStepAsync(session, material, "Router",
                JsonSerializer.SerializeToElement(new { selectedAgent = target.Id }), target.Id, cancellationToken);
        var agent = material.Agents.FirstOrDefault(value => string.Equals(value.AgentName, target.Id, StringComparison.Ordinal)
            && (target.Namespace is null || string.Equals(value.AgentNamespace,
                target.Namespace.Value.Value, StringComparison.Ordinal)))
            ?? material.Agents.FirstOrDefault(value => string.Equals(value.AgentName, target.Id, StringComparison.Ordinal))
            ?? throw new AwpExecutionNotSupportedException("flow_agent_material_missing", $"Agent '{target.Id}' is absent from the execution material.");
        var step = await OpenStepAsync(session, material, "Agent", cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.StepStarted,
            new(material.RunId, FlowStep: step), null, null, cancellationToken);
        var output = await ExecuteFlowAgentAsync(session, material, agent, step, material.Input, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.StepCompleted,
            new(material.RunId, FlowStep: step), JsonSerializer.SerializeToElement(new
            {
                status = "succeeded",
                output,
                agentResourceId = agent.AgentName,
                agentVersion = agent.Generation
            }), null, cancellationToken);
        await CompleteStepAsync(session, material, "Output", output, null, cancellationToken);
        return output;
    }

    private async Task<AwpAssignmentExecutionResult> ExecuteGraphAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var current = graph.EntryStep;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? transitionOutput = null;
        AwpAssignmentExecutionResult? final = null;
        var cleanupArtifacts = new List<AwpCapturedFlowArtifact>();
        try
        {
        while (visited.Add(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepDefinition = graph.Steps.Single(value => value.Name == current);
            var context = ExecutionContext(material, stepDefinition, outputs, transitionOutput);
            var step = await OpenStepAsync(session, material, current, cancellationToken);
            var location = new AwpExecutionLocation(material.RunId, FlowStep: step);
            await session.AppendEventAsync(AwpExecutionEventKind.StepStarted, location, null, null, cancellationToken);
            JsonElement? output;
            var eventName = "completed";
            AwpExecutionAgentMaterial? executedAgent = null;
            AwpInvokeFlowToolResponse? executedTool = null;
            var childRunIds = new List<string>();
            var stepArtifacts = new List<AwpFlowArtifact>();
            string? artifactStorageFlowRunId = null;
            int? repeatIteration = null;
            string? errorCode = null;
            string? errorMessage = null;
            switch (stepDefinition)
            {
                case InputFlowStepDefinition:
                    output = material.Input.Clone();
                    break;
                case AgentFlowStepDefinition agentStep:
                    executedAgent = material.Agents.SingleOrDefault(value => value.ParticipantId == agentStep.Name)
                        ?? material.Agents.SingleOrDefault(value => value.AgentName == agentStep.Agent.ResourceId)
                        ?? throw new AwpExecutionNotSupportedException("flow_agent_material_missing", $"Agent for step '{agentStep.Name}' is absent from the execution material.");
                    var agentInput = agentStep.InputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(agentStep.InputMapping.Value, context, cancellationToken);
                    try
                    {
                        output = await ExecuteFlowAgentAsync(session, material, executedAgent, step,
                            agentInput, cancellationToken);
                        eventName = "success";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message });
                        eventName = "error";
                        errorCode = exception is AwpExecutionNotSupportedException unsupported
                            ? unsupported.Code : "agent_step_failed";
                        errorMessage = exception.Message;
                    }
                    break;
                case RouterFlowStepDefinition router:
                    var selection = router.Candidates.FirstOrDefault(candidate => material.Input.GetRawText()
                        .Contains(candidate.Route, StringComparison.OrdinalIgnoreCase));
                    if (selection is null && router.Fallback is null)
                        throw new AwpExecutionNotSupportedException("router_no_route", $"Router step '{router.Name}' could not select a route.");
                    output = JsonSerializer.SerializeToElement(new
                    {
                        selectedRoute = selection?.Route ?? "fallback",
                        selectedAgent = selection?.Agent.ResourceId ?? router.Fallback!.ResourceId
                    });
                    eventName = selection is null ? "fallback" : "selected";
                    break;
                case ConditionFlowStepDefinition condition:
                    var conditionResult = await EvaluateConditionAsync(condition, context, cancellationToken);
                    output = JsonSerializer.SerializeToElement(conditionResult);
                    eventName = conditionResult ? "true" : "false";
                    break;
                case TransformFlowStepDefinition transform:
                    output = transform.Mode.Equals("Expression", StringComparison.OrdinalIgnoreCase)
                        ? await EvaluateExpressionAsync(transform.Expression!, context, cancellationToken)
                        : transform.Mapping is null
                            ? JsonSerializer.SerializeToElement(new { })
                            : await ResolveJsonAsync(transform.Mapping.Value, context, cancellationToken);
                    break;
                case ToolFlowStepDefinition:
                case ToolRouteFlowStepDefinition:
                    var mapping = stepDefinition switch
                    {
                        ToolFlowStepDefinition directTool => directTool.ArgumentsMapping,
                        ToolRouteFlowStepDefinition routedTool => routedTool.ArgumentsMapping,
                        _ => null
                    };
                    var arguments = mapping is null
                        ? JsonSerializer.SerializeToElement(new { })
                        : await ResolveJsonAsync(mapping.Value, context, cancellationToken);
                    try
                    {
                        executedTool = await session.Client.InvokeFlowToolAsync(new(session.Context,
                            step.StepExecutionId, arguments), cancellationToken);
                        output = executedTool.Result?.Clone();
                        eventName = "success";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message });
                        eventName = "error";
                        errorCode = exception is AwpClientException client ? client.Code
                            : stepDefinition is ToolRouteFlowStepDefinition
                                ? "tool_route_step_failed" : "tool_step_failed";
                        errorMessage = exception.Message;
                    }
                    break;
                case FlowCallStepDefinition flowCall:
                    var childInput = flowCall.InputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(flowCall.InputMapping.Value, context, cancellationToken);
                    var child = await ExecuteChildFlowAsync(session, step, childInput,
                        "flowCall", null, cancellationToken);
                    childRunIds.Add(child.RunId);
                    output = child.Result.Output?.Clone();
                    eventName = child.Result.OutputName
                        ?? (child.Result.Outcome == "error" ? "failed" : "completed");
                    if (child.Result.Outcome == "error")
                    {
                        errorCode = child.Result.ErrorCode ?? "child_flow_failed";
                        errorMessage = child.Result.ErrorMessage ?? "The child Flow failed.";
                    }
                    break;
                case RepeatFlowStepDefinition repeat:
                    var repeatInput = repeat.InputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(repeat.InputMapping.Value, context, cancellationToken);
                    output = null;
                    for (var iteration = 1; iteration <= repeat.MaximumIterations; iteration++)
                    {
                        repeatIteration = iteration;
                        var repeated = await ExecuteChildFlowAsync(session, step, repeatInput,
                            "repeat", iteration, cancellationToken);
                        childRunIds.Add(repeated.RunId);
                        output = repeated.Result.Output?.Clone();
                        if (repeated.Result.Outcome == "error")
                        {
                            eventName = repeated.Result.OutputName ?? "failed";
                            errorCode = repeated.Result.ErrorCode ?? "child_flow_failed";
                            errorMessage = repeated.Result.ErrorMessage ?? "The repeated child Flow failed.";
                            break;
                        }
                        outputs[current] = output?.Clone();
                        var repeatContext = ExecutionContext(material, stepDefinition, outputs,
                            output, output);
                        var until = await EvaluateExpressionAsync(repeat.Until, repeatContext, cancellationToken);
                        if (until?.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                            throw new AwpExecutionNotSupportedException("flow_repeat_until_invalid",
                                $"Repeat step '{repeat.Name}' until expression must return a boolean.");
                        if (until.Value.ValueKind == JsonValueKind.True) break;
                        if (iteration == repeat.MaximumIterations)
                            throw new AwpExecutionNotSupportedException("flow_repeat_limit_exceeded",
                                $"Repeat step '{repeat.Name}' reached its maximum of {repeat.MaximumIterations} iterations.");
                        repeatInput = repeat.NextInputMapping is null
                            ? output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                            : await ResolveJsonAsync(repeat.NextInputMapping.Value, repeatContext, cancellationToken);
                    }
                    break;
                case OutputFlowStepDefinition outputStep:
                    output = outputStep.OutputMapping is null
                        ? transitionOutput?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                        : await ResolveJsonAsync(outputStep.OutputMapping.Value, context, cancellationToken);
                    var outcome = outputStep.Outcome ?? FlowOutputOutcome.Success;
                    eventName = outputStep.Outcome.HasValue ? outputStep.Name : "completed";
                    if (outcome == FlowOutputOutcome.Error)
                    {
                        errorCode = outputStep.Code ?? "FLOW_FAILED";
                        errorMessage = outputStep.Message ?? "Flow execution failed.";
                    }
                    final = new(output, outputStep.Outcome.HasValue ? outputStep.Name : null,
                        outcome == FlowOutputOutcome.Error ? "error" : "success",
                        errorCode, errorMessage,
                        outputStep.DetailsExpression is null ? null
                            : (await ResolveStringAsync(outputStep.DetailsExpression, context, cancellationToken)));
                    break;
                case FailureFlowStepDefinition failure:
                    output = JsonSerializer.SerializeToElement(new { error = failure.Message, code = failure.Code });
                    eventName = failure.Name;
                    errorCode = failure.Code;
                    errorMessage = failure.Message;
                    final = new(output, null, "error", failure.Code, failure.Message,
                        failure.DetailsExpression);
                    break;
                default:
                    throw new AwpExecutionNotSupportedException("flow_step_type_unsupported",
                        $"Step '{stepDefinition.Name}' of type '{stepDefinition.Type()}' is not yet supported by the external Worker.");
            }
            outputs[current] = output?.Clone();
            if (errorCode is null && stepDefinition.ArtifactOutput is { } artifactOutput)
            {
                var artifactContext = ExecutionContext(material, stepDefinition, outputs,
                    transitionOutput, output);
                var fileName = artifactOutput.FileName is null ? null
                    : await ResolveStringAsync(artifactOutput.FileName, artifactContext, cancellationToken);
                var mediaType = await ResolveStringAsync(artifactOutput.MediaType,
                    artifactContext, cancellationToken)
                    ?? throw new AwpExecutionNotSupportedException("flow_step_artifact_media_type_unresolved",
                        "The configured Artifact media type did not resolve a value.");
                var content = artifactOutput.ContentMapping is null
                    ? output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null)
                    : await ResolveJsonAsync(artifactOutput.ContentMapping.Value, artifactContext, cancellationToken);
                var provenance = ArtifactProvenance(stepDefinition, executedAgent, executedTool, childRunIds);
                var captured = (await session.Client.CaptureFlowArtifactAsync(new(
                    session.Context, step.StepExecutionId, fileName, mediaType, content, provenance),
                    cancellationToken)).Artifact;
                var projected = captured;
                cleanupArtifacts.Add(new(step, captured, artifactOutput.Clean));
                if (artifactOutput.StorageFlow is not null)
                {
                    var storageInput = JsonSerializer.SerializeToElement(new
                    {
                        stagedArtifactId = captured.ArtifactId,
                        producerFlowRunId = material.RunId,
                        producerFlowStepId = stepDefinition.Name
                    });
                    var storage = await ExecuteChildFlowAsync(session, step, storageInput,
                        "artifactStorage", null, cancellationToken);
                    artifactStorageFlowRunId = storage.RunId;
                    childRunIds.Add(storage.RunId);
                    if (storage.Result.Outcome == "error")
                    {
                        errorCode = storage.Result.ErrorCode ?? "artifact_storage_flow_failed";
                        errorMessage = storage.Result.ErrorMessage ?? "The Artifact storage Flow failed.";
                        eventName = "failed";
                    }
                    else
                    {
                        var durableId = DurableArtifactId(storage.Result.Output, storage.RunId);
                        projected = new(durableId, captured.FileName, captured.MediaType, "durable",
                            storage.RunId, captured.LocalArtifactId ?? captured.ArtifactId);
                    }
                }
                stepArtifacts.Add(projected);
            }
            var candidates = graph.Transitions
                .Where(value => value.FromStep == current && string.Equals(value.Event, eventName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.Priority ?? int.MaxValue)
                .ToArray();
            FlowTransitionDefinition? transition = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Condition is null
                    || (await EvaluateExpressionAsync(candidate.Condition,
                        new(material.Input, outputs, output), cancellationToken))?.ValueKind == JsonValueKind.True)
                {
                    transition = candidate;
                    break;
                }
            }
            await session.AppendEventAsync(AwpExecutionEventKind.StepCompleted, location,
                JsonSerializer.SerializeToElement(new
                {
                    status = errorCode is null ? "succeeded" : "failed",
                    output,
                    selectedTransition = transition?.Id,
                    agentResourceId = executedAgent?.AgentName,
                    agentVersion = executedAgent?.Generation,
                    tool = executedTool is null ? null : new
                    {
                        executedTool.ToolName,
                        executedTool.ToolNamespace,
                        executedTool.ToolUid,
                        executedTool.ToolGeneration,
                        executedTool.ProviderName,
                        executedTool.ProviderNamespace,
                        executedTool.ProviderType,
                        executedTool.ExternalToolId
                    },
                    toolRoute = executedTool?.Route,
                    childFlowRunIds = childRunIds,
                    artifactStorageFlowRunId,
                    repeatIteration,
                    artifacts = stepArtifacts,
                    errorCode,
                    error = errorMessage
                }), null, cancellationToken);
            if (stepDefinition is OutputFlowStepDefinition or FailureFlowStepDefinition) break;
            if (transition is null && errorCode is not null)
                throw new AwpExecutionNotSupportedException(errorCode, errorMessage ?? "The Flow step failed.");
            if (transition is null)
                throw new AwpExecutionNotSupportedException("flow_transition_missing",
                    $"No '{eventName}' transition leaves step '{current}'.");
            current = transition.ToStep;
            transitionOutput = output?.Clone();
        }
        }
        finally
        {
            foreach (var captured in cleanupArtifacts.Where(value =>
                value.Cleanup != FlowStepArtifactCleanupMode.Never))
            {
                try
                {
                    await session.Client.CleanupFlowArtifactAsync(new(session.Context,
                        captured.Step.StepExecutionId, captured.Artifact), CancellationToken.None);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Could not clean staged Artifact {ArtifactId} for Flow Run {FlowRunId}, step {StepDefinitionId}",
                        captured.Artifact.ArtifactId, material.RunId, captured.Step.StepDefinitionId);
                }
            }
        }
        if (final is null)
            throw new AwpExecutionNotSupportedException("flow_output_missing", "The Flow completed without reaching an Output step.");
        return final;
    }

    private async Task<AwpChildExecutionResult> ExecuteChildFlowAsync(
        AwpAssignmentSession session,
        AwpFlowStepLocation step,
        JsonElement input,
        string purpose,
        int? iteration,
        CancellationToken cancellationToken)
    {
        var child = await session.Client.CreateChildFlowAsync(new(session.Context,
            step.StepExecutionId, input, purpose, iteration), cancellationToken);
        if (child.Material is null)
            throw new AwpExecutionNotSupportedException("child_flow_material_missing",
                $"Child Flow Run '{child.RunId}' has no execution material.");
        await session.AppendEventAsync(AwpExecutionEventKind.RunStarted,
            new(child.RunId), null, null, cancellationToken);
        try
        {
            var result = await ExecuteFlowAsync(session, child.Material, cancellationToken);
            await session.AppendEventAsync(AwpExecutionEventKind.RunCompleted,
                new(child.RunId), JsonSerializer.SerializeToElement(new
                {
                    status = result.Outcome == "error" ? "failed" : "succeeded",
                    output = result.Output,
                    outputName = result.OutputName,
                    outcome = result.Outcome,
                    errorCode = result.ErrorCode,
                    error = result.ErrorMessage,
                    errorDetails = result.ErrorDetails
                }), null, cancellationToken);
            return new(child.RunId, result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                AwpExecutionNotSupportedException unsupported => unsupported.Code,
                AwpClientException client => client.Code,
                _ => "child_flow_execution_failed"
            };
            var result = new AwpAssignmentExecutionResult(null, null, "error", code, exception.Message);
            await session.AppendEventAsync(AwpExecutionEventKind.RunCompleted,
                new(child.RunId), JsonSerializer.SerializeToElement(new
                {
                    status = "failed",
                    outcome = "error",
                    errorCode = code,
                    error = exception.Message
                }), null, cancellationToken);
            return new(child.RunId, result);
        }
    }

    private async Task<JsonElement> ExecuteFlowAgentAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, AwpExecutionAgentMaterial agent,
        AwpFlowStepLocation step, JsonElement input, CancellationToken cancellationToken)
    {
        var turn = (await session.Client.OpenTurnAsync(new(session.Context, material.RunId, step, agent.ParticipantId),
            Guid.NewGuid(), cancellationToken)).Turn;
        var location = new AwpExecutionLocation(material.RunId, step, turn);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnStarted, location,
            JsonSerializer.SerializeToElement(new { participantId = agent.ParticipantId }), null, cancellationToken);
        var chatClient = new AwpModelChatClient(session, agent, turn);
        var toolPipeline = new AwpToolExecutionPipeline(session, agent, turn);
        var factory = new AgentFrameworkRuntimeFactory(new AwpChatClientResolver(chatClient), loggerFactory,
            new GenAiObservabilityOptions { Enabled = true });
        var runtime = await factory.CreateAsync(ToDefinition(agent), agent.RevisionId,
            new AgentRuntimeContext(new AwpToolCatalog(agent.Tools), toolPipeline), cancellationToken);
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest(input.GetRawText(), material.RunId,
            Execution: new AgentExecutionOptions { Streaming = RuntimeStreamingMode.Disabled },
            ToolExecution: new ToolExecutionScope
            {
                OwnerKind = ToolExecutionOwnerKind.FlowRun,
                TenantId = session.Assignment.Scope.TenantId,
                WorkspaceId = ParseWorkspace(session.Assignment.Scope.WorkspaceId),
                ExecutionId = material.RunId,
                FlowStepId = step.StepExecutionId.Value.ToString("D"),
                AgentGeneration = agent.Generation
            }), cancellationToken);
        if (!string.IsNullOrEmpty(result.Output))
            await session.AppendEventAsync(AwpExecutionEventKind.ResponseDelta, location,
                JsonSerializer.SerializeToElement(new { content = result.Output }), null, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnCompleted, location,
            JsonSerializer.SerializeToElement(new { participantId = agent.ParticipantId }), null, cancellationToken);
        return JsonSerializer.SerializeToElement(result.Output);
    }

    private async Task<AwpFlowStepLocation> OpenStepAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, string stepDefinitionId, CancellationToken cancellationToken) =>
        (await session.Client.OpenStepAsync(new(session.Context, material.RunId, material.FlowVersion,
            material.FlowDefinitionHash, stepDefinitionId), Guid.NewGuid(), cancellationToken)).Step;

    private async Task CompleteStepAsync(AwpAssignmentSession session, AwpRootFlowExecutionMaterial material,
        string stepDefinitionId, JsonElement? output, string? selectedTransition, CancellationToken cancellationToken)
    {
        var step = await OpenStepAsync(session, material, stepDefinitionId, cancellationToken);
        var location = new AwpExecutionLocation(material.RunId, FlowStep: step);
        await session.AppendEventAsync(AwpExecutionEventKind.StepStarted, location, null, null, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.StepCompleted, location,
            JsonSerializer.SerializeToElement(new { status = "succeeded", output, selectedTransition }), null, cancellationToken);
    }

    private static FlowTargetReference SelectTarget(RoutingFlowDefinition routing, JsonElement input) =>
        routing.Destinations.FirstOrDefault(destination => input.GetRawText().Contains(destination.Id, StringComparison.OrdinalIgnoreCase))
        ?? routing.Fallback ?? routing.Destinations[0];

    private static JsonElement ResolveMappedInput(JsonElement? mapping, JsonElement fallback,
        IReadOnlyDictionary<string, JsonElement?> outputs)
    {
        if (mapping is null) return outputs.Values.LastOrDefault()?.Clone() ?? fallback.Clone();
        return mapping.Value.Clone();
    }

    private static async ValueTask<JsonElement?> EvaluateExpressionAsync(string source,
        FlowExecutionContext context, CancellationToken cancellationToken)
    {
        var expressions = new FlowExpressionParser();
        var parsed = expressions.Parse(source);
        if (!parsed.IsValid)
            throw new AwpExecutionNotSupportedException("expression_invalid", parsed.Error!);
        return await expressions.EvaluateAsync(parsed.Expression!, context, cancellationToken);
    }

    private static async Task<string?> ResolveStringAsync(string source,
        FlowExecutionContext context, CancellationToken cancellationToken) =>
        source.StartsWith("${", StringComparison.Ordinal)
            ? (await EvaluateExpressionAsync(source, context, cancellationToken))?.ToString()
            : source;

    private static FlowExecutionContext ExecutionContext(
        AwpRootFlowExecutionMaterial material,
        FlowStepDefinition step,
        IReadOnlyDictionary<string, JsonElement?> outputs,
        JsonElement? transitionOutput,
        JsonElement? currentStepOutput = null) => new(
            material.Input,
            outputs,
            transitionOutput,
            new(
                material.RunId,
                material.RootFlowRunId ?? material.RunId,
                material.ParentFlowRunId,
                step.Name,
                material.CorrelationId,
                step.DisplayName),
            currentStepOutput);

    private static IReadOnlyDictionary<string, string> ArtifactProvenance(
        FlowStepDefinition step,
        AwpExecutionAgentMaterial? agent,
        AwpInvokeFlowToolResponse? tool,
        IReadOnlyList<string> childRunIds)
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
            }
        };
        if (agent is not null)
        {
            provenance["agentResourceId"] = agent.AgentName;
            provenance["agentVersion"] = agent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            provenance["modelProfileResourceId"] = agent.ModelProfileName;
        }
        if (tool is not null)
        {
            provenance["toolName"] = tool.ToolName;
            provenance["toolNamespace"] = tool.ToolNamespace;
            provenance["toolUid"] = tool.ToolUid.ToString("D");
            provenance["toolGeneration"] = tool.ToolGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            provenance["toolExternalId"] = tool.ExternalToolId;
            provenance["providerName"] = tool.ProviderName;
            provenance["providerNamespace"] = tool.ProviderNamespace;
            provenance["providerType"] = tool.ProviderType;
            if (tool.Route is not null)
            {
                provenance["toolSetName"] = tool.Route.ToolSetName;
                provenance["toolSetNamespace"] = tool.Route.ToolSetNamespace;
                provenance["toolSetVersion"] = tool.Route.ToolSetVersion;
                provenance["toolRoute"] = tool.Route.Route;
            }
        }
        if (childRunIds.LastOrDefault() is { } childRunId)
            provenance["childFlowRunId"] = childRunId;
        return provenance;
    }

    private static string DurableArtifactId(JsonElement? output, string storageRunId)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("flowRunArtifactId", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new AwpExecutionNotSupportedException("flow_step_artifact_storage_result_invalid",
                $"Artifact storage Flow Run '{storageRunId}' did not return a flowRunArtifactId.");
        return id.GetString()!;
    }

    private static async Task<bool> EvaluateConditionAsync(ConditionFlowStepDefinition condition,
        FlowExecutionContext context, CancellationToken cancellationToken)
    {
        if (condition.Mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase))
            return (await EvaluateExpressionAsync(condition.Expression!, context, cancellationToken))?.ValueKind
                == JsonValueKind.True;
        var left = condition.Left?.StartsWith("${", StringComparison.Ordinal) == true
            ? await EvaluateExpressionAsync(condition.Left, context, cancellationToken)
            : JsonSerializer.SerializeToElement(condition.Left);
        var leftText = left?.ToString() ?? string.Empty;
        var right = condition.Right;
        return condition.Operator.ToLowerInvariant() switch
        {
            "equals" => string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "not equals" => !string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "contains" => leftText.Contains(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "starts with" => leftText.StartsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "ends with" => leftText.EndsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "greater than" => Compare(leftText, right) > 0,
            "greater than or equal" => Compare(leftText, right) >= 0,
            "less than" => Compare(leftText, right) < 0,
            "less than or equal" => Compare(leftText, right) <= 0,
            "is empty" => string.IsNullOrEmpty(leftText),
            "is not empty" => !string.IsNullOrEmpty(leftText),
            _ => throw new AwpExecutionNotSupportedException("condition_operator_unsupported",
                $"Condition operator '{condition.Operator}' is not supported.")
        };
    }

    private static int Compare(string left, string? right)
    {
        if (decimal.TryParse(left, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> ResolveJsonAsync(JsonElement value,
        FlowExecutionContext context, CancellationToken cancellationToken)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            return text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}')
                ? (await EvaluateExpressionAsync(text, context, cancellationToken))?.Clone()
                    ?? JsonSerializer.SerializeToElement<object?>(null)
                : value.Clone();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                result[property.Name] = await ResolveJsonAsync(property.Value, context, cancellationToken);
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<JsonElement>();
            foreach (var item in value.EnumerateArray())
                result.Add(await ResolveJsonAsync(item, context, cancellationToken));
            return JsonSerializer.SerializeToElement(result);
        }
        return value.Clone();
    }

    private static ExecutableAgentDefinition ToDefinition(AwpExecutionAgentMaterial agent) => new()
    {
        AgentId = agent.AgentId,
        AgentKey = agent.AgentName,
        DisplayName = agent.DisplayName,
        Description = agent.Description,
        AgentVersion = agent.Generation,
        EffectiveInstructions = agent.Instructions,
        ModelProfileName = agent.ModelProfileName,
        ModelProfileNamespace = ResourceNamespace.Parse(agent.ModelProfileNamespace),
        RuntimeProfileName = agent.RuntimeProfileName,
        RuntimeProfileNamespace = ResourceNamespace.Parse(agent.RuntimeProfileNamespace),
        EffectiveToolNames = agent.Tools.Select(tool => tool.Id).ToArray(),
        MiddlewareIds = [],
        ContextProviderIds = [],
        Capabilities = [],
        Handler = agent.Handler,
        DefinitionHash = agent.DefinitionHash
    };

    private static string FormatInput(AwpDirectAgentExecutionMaterial material)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(material.Context)) builder.AppendLine(material.Context);
        foreach (var message in material.Messages)
            builder.Append(message.Role).Append(": ").AppendLine(message.Content);
        return builder.ToString().Trim();
    }

    private static WorkspaceId ParseWorkspace(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? new WorkspaceId(id)
        : throw new InvalidOperationException("The assignment Workspace identity is invalid.");
}

internal sealed class AwpExecutionNotSupportedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record AwpAssignmentExecutionResult(
    JsonElement? Output,
    string? OutputName = null,
    string? Outcome = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ErrorDetails = null);

internal sealed record AwpChildExecutionResult(string RunId, AwpAssignmentExecutionResult Result);

internal sealed record AwpCapturedFlowArtifact(
    AwpFlowStepLocation Step,
    AwpFlowArtifact Artifact,
    FlowStepArtifactCleanupMode Cleanup);
