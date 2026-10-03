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

internal sealed class AwpAssignmentExecutor(ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<JsonElement?> ExecuteAsync(AwpAssignmentSession session, AwpExecutionMaterial material,
        CancellationToken cancellationToken) => material switch
    {
        AwpDirectAgentExecutionMaterial direct => await ExecuteDirectAsync(session, direct, cancellationToken),
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
            RuntimeProfileName = AwpRuntimeKinds.MicrosoftAgentFramework,
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

    private async Task<JsonElement?> ExecuteFlowAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, CancellationToken cancellationToken)
    {
        var version = material.Definition.Deserialize<FlowVersion>(JsonOptions)
            ?? throw new AwpExecutionNotSupportedException("flow_material_invalid", "The assigned Flow definition is invalid.");
        if (version.Graph is not null)
            return await ExecuteGraphAsync(session, material, version.Graph, cancellationToken);
        return version.Definition switch
        {
            DirectFlowDefinition direct => await ExecuteSimpleAsync(session, material, direct.Target, false, cancellationToken),
            RoutingFlowDefinition routing => await ExecuteSimpleAsync(session, material,
                SelectTarget(routing, material.Input), true, cancellationToken),
            OrchestrationFlowDefinition orchestration => await ExecuteOrchestrationAsync(session, material,
                orchestration, cancellationToken),
            _ => throw new AwpExecutionNotSupportedException("flow_kind_unsupported", "The assigned Flow kind is unsupported by this Worker.")
        };
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
                ParseWorkspace(session.Assignment.Scope.WorkspaceId), Guid.Empty)), cancellationToken))
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
            && (target.Namespace is null || string.Equals(value.ModelProfileNamespace, target.Namespace.Value.Value, StringComparison.Ordinal)))
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

    private async Task<JsonElement?> ExecuteGraphAsync(AwpAssignmentSession session,
        AwpRootFlowExecutionMaterial material, FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var current = graph.EntryStep;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? final = null;
        while (visited.Add(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepDefinition = graph.Steps.Single(value => value.Name == current);
            var context = new FlowExecutionContext(material.Input, outputs,
                outputs.Values.LastOrDefault());
            var step = await OpenStepAsync(session, material, current, cancellationToken);
            var location = new AwpExecutionLocation(material.RunId, FlowStep: step);
            await session.AppendEventAsync(AwpExecutionEventKind.StepStarted, location, null, null, cancellationToken);
            JsonElement? output;
            var eventName = "completed";
            AwpExecutionAgentMaterial? executedAgent = null;
            switch (stepDefinition)
            {
                case InputFlowStepDefinition:
                    output = material.Input.Clone();
                    break;
                case AgentFlowStepDefinition agentStep:
                    executedAgent = material.Agents.SingleOrDefault(value => value.ParticipantId == agentStep.Name)
                        ?? material.Agents.SingleOrDefault(value => value.AgentName == agentStep.Agent.ResourceId)
                        ?? throw new AwpExecutionNotSupportedException("flow_agent_material_missing", $"Agent for step '{agentStep.Name}' is absent from the execution material.");
                    output = await ExecuteFlowAgentAsync(session, material, executedAgent, step,
                        ResolveMappedInput(agentStep.InputMapping, material.Input, outputs), cancellationToken);
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
                case FlowCallStepDefinition flowCall:
                    var childInput = ResolveMappedInput(flowCall.InputMapping,
                        outputs.Values.LastOrDefault() ?? material.Input, outputs);
                    var child = await session.Client.CreateChildFlowAsync(new(session.Context,
                        step.StepExecutionId, childInput), cancellationToken);
                    if (child.Material is null)
                        throw new AwpExecutionNotSupportedException("child_flow_material_missing",
                            $"Child Flow Run '{child.RunId}' has no execution material.");
                    await session.AppendEventAsync(AwpExecutionEventKind.RunStarted,
                        new(child.RunId), null, null, cancellationToken);
                    try
                    {
                        output = await ExecuteFlowAsync(session, child.Material, cancellationToken);
                        await session.AppendEventAsync(AwpExecutionEventKind.RunCompleted,
                            new(child.RunId), JsonSerializer.SerializeToElement(new { status = "succeeded", output }),
                            null, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await session.AppendEventAsync(AwpExecutionEventKind.RunCompleted,
                            new(child.RunId), JsonSerializer.SerializeToElement(new
                            {
                                status = "failed",
                                errorCode = exception is AwpExecutionNotSupportedException unsupported
                                    ? unsupported.Code : "child_flow_execution_failed",
                                error = exception.Message
                            }), null, cancellationToken);
                        throw;
                    }
                    break;
                case OutputFlowStepDefinition outputStep:
                    output = ResolveMappedInput(outputStep.OutputMapping,
                        outputs.Values.LastOrDefault() ?? material.Input, outputs);
                    final = output;
                    break;
                case FailureFlowStepDefinition failure:
                    throw new AwpExecutionNotSupportedException(failure.Code, failure.Message);
                default:
                    throw new AwpExecutionNotSupportedException("flow_step_type_unsupported",
                        $"Step '{stepDefinition.Name}' of type '{stepDefinition.Type()}' is not yet supported by the external Worker.");
            }
            outputs[current] = output?.Clone();
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
                    status = "succeeded",
                    output,
                    selectedTransition = transition?.Id,
                    agentResourceId = executedAgent?.AgentName,
                    agentVersion = executedAgent?.Generation
                }), null, cancellationToken);
            if (stepDefinition is OutputFlowStepDefinition) break;
            if (transition is null)
                throw new AwpExecutionNotSupportedException("flow_transition_missing",
                    $"No '{eventName}' transition leaves step '{current}'.");
            current = transition.ToStep;
        }
        if (final is null)
            throw new AwpExecutionNotSupportedException("flow_output_missing", "The Flow completed without reaching an Output step.");
        return final;
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
        RuntimeProfileName = AwpRuntimeKinds.MicrosoftAgentFramework,
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
