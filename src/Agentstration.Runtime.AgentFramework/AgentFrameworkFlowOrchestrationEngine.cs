using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.AgentFramework;

#pragma warning disable MAAIW001
public sealed class AgentFrameworkFlowOrchestrationEngine(
    IRuntimeAgentResolver agentResolver,
    IToolCatalog tools,
    AgentFrameworkRuntimeFactory agentFactory,
    IRuntimeExecutionStateStore? executionStates = null,
    TimeProvider? timeProvider = null,
    IToolExecutionPipeline? configuredToolExecution = null) : IFlowOrchestrationEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CheckpointAvailabilityPollInterval = TimeSpan.FromMilliseconds(10);
    private const int CheckpointAvailabilityPollAttempts = 1000;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IToolExecutionPipeline toolExecution = configuredToolExecution ?? UnavailableToolExecutionPipeline.Instance;

    public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var references = request.Definition.Pattern is MagenticOrchestrationPattern magentic
            ? request.Definition.Participants.Concat([magentic.Manager]).ToArray()
            : request.Definition.Participants;
        var participants = await ResolveParticipantsAsync(references, request.RuntimeBindings, ToolScope(request), cancellationToken);
        var bindings = references.Select(reference => participants[reference.Id].Binding).ToArray();
        yield return new FlowRuntimeBindingsResolved(bindings);
        var built = await BuildWorkflowAsync(request.Definition, participants, cancellationToken);
        var actorsByExecutorId = MapExecutors(built.Workflow, participants, built.Manager);
        var terminationPhrase = (request.Definition.Pattern as HandoffOrchestrationPattern)?.TerminationPhrase;
        var states = request.Definition.Participants.ToDictionary(
            participant => participant.Id,
            participant => new ParticipantState(participants[participant.Id], terminationPhrase),
            StringComparer.Ordinal);
        var nextTurn = 0;
        var serializedTurns = request.Definition.Pattern is not ConcurrentOrchestrationPattern;
        List<ChatMessage>? finalMessages = null;
        var unsupportedOutputTypes = new HashSet<string>(StringComparer.Ordinal);

        CheckpointManager? checkpointManager = null;
        if (executionStates is not null)
            checkpointManager = CheckpointManager.CreateJson(new AgentFrameworkCheckpointStore(executionStates, request.WorkspaceId, timeProvider));
        await using var run = request.RuntimeState is null
            ? checkpointManager is null
                ? await InProcessExecution.RunStreamingAsync(
                    built.Workflow,
                    new List<ChatMessage> { new(ChatRole.User, Prompt(request.Input)) },
                    request.RunId,
                    cancellationToken)
                : await InProcessExecution.RunStreamingAsync(
                    built.Workflow,
                    new List<ChatMessage> { new(ChatRole.User, Prompt(request.Input)) },
                    checkpointManager,
                    request.RunId,
                    cancellationToken)
            : checkpointManager is null
                ? throw new FlowValidationException("flow_runtime_state_store_unavailable", "Durable runtime state storage is required to resume this orchestration.")
                : await InProcessExecution.ResumeStreamingAsync(
                    built.Workflow,
                    new CheckpointInfo(request.RunId, request.RuntimeState.StateId),
                    checkpointManager,
                    cancellationToken);
        if (request.RuntimeState is null)
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            switch (workflowEvent)
            {
                case WorkflowErrorEvent error:
                    throw new FlowValidationException(
                        "flow_orchestration_framework_failed",
                        error.Exception?.GetBaseException().Message ?? "Microsoft Agent Framework reported an orchestration failure.");

                case RequestInfoEvent interaction:
                    if (request.AnsweredInput?.Response is not null
                        && string.Equals(request.AnsweredInput.RuntimeRequestId, interaction.Request.RequestId, StringComparison.Ordinal))
                    {
                        await run.SendResponseAsync(CreateResponse(interaction.Request, request.AnsweredInput));
                        break;
                    }
                    var sourceState = states.Values
                        .Where(value => value.Active is not null)
                        .OrderByDescending(value => value.Active!.Turn)
                        .FirstOrDefault();
                    var prompt = sourceState is not null
                        ? sourceState.Active?.Content.ToString()
                        : null;
                    for (var attempt = 0; checkpointManager is not null && run.LastCheckpoint is null && attempt < CheckpointAvailabilityPollAttempts; attempt++)
                        await Task.Delay(CheckpointAvailabilityPollInterval, timeProvider, cancellationToken);
                    if (checkpointManager is null || run.LastCheckpoint is null)
                        throw new FlowValidationException(
                            "flow_orchestration_interaction_not_durable",
                            "The orchestration requested external input before durable runtime state was available.");
                    var interactionDescription = DescribeInteraction(interaction.Request, prompt);
                    yield return new FlowExternalInputRequested(
                        interaction.Request.RequestId,
                        interactionDescription.Prompt,
                        interactionDescription.Type,
                        [],
                        sourceState?.Participant.Id,
                        new DurableRuntimeStateReference(
                            AgentFrameworkCheckpointStore.RuntimeType,
                            run.LastCheckpoint.CheckpointId,
                            timeProvider.GetUtcNow()));
                    yield break;

                case AgentResponseUpdateEvent update:
                    var actor = ResolveActor(update.Update.AgentId, update.ExecutorId, participants, built.Manager, actorsByExecutorId);
                    if (actor.IsManager) break;
                    if (serializedTurns)
                    {
                        foreach (var previousState in states.Values.Where(value => value.Active is not null && value.Participant.Id != actor.Id).OrderBy(value => value.Active!.Turn))
                        {
                            var previousTurn = previousState.CompleteActive();
                            if (!string.IsNullOrEmpty(previousTurn.Delta))
                                yield return new FlowParticipantDelta(previousState.Participant.Id, previousTurn.Delta);
                            if (previousTurn.Include)
                                yield return new FlowParticipantTurnCompleted(previousState.Participant.Id, previousTurn.Turn.Turn);
                        }
                    }
                    var state = states[actor.Id];
                    var responseId = ResponseKey(update.Update.ResponseId, update.Update.MessageId, state.Active?.ResponseId);
                    if (state.IsCompleted(responseId))
                    {
                        state.Capture(update.Update.Contents);
                        break;
                    }
                    if (state.Active is null)
                    {
                        state.Start(++nextTurn, responseId);
                        yield return new FlowParticipantTurnStarted(actor.Id, nextTurn);
                    }
                    else
                    {
                        state.Correlate(responseId);
                    }
                    state.Capture(update.Update.Contents);
                    if (!string.IsNullOrEmpty(update.Update.Text))
                    {
                        var delta = state.Append(update.Update.Text);
                        if (!string.IsNullOrEmpty(delta))
                            yield return new FlowParticipantDelta(actor.Id, delta);
                    }
                    if (update.Update.FinishReason is not null)
                    {
                        var completed = state.CompleteActive();
                        if (!string.IsNullOrEmpty(completed.Delta))
                            yield return new FlowParticipantDelta(actor.Id, completed.Delta);
                        if (completed.Include)
                            yield return new FlowParticipantTurnCompleted(actor.Id, completed.Turn.Turn);
                    }
                    break;

                case AgentResponseEvent response:
                    actor = ResolveActor(response.Response.AgentId, response.ExecutorId, participants, built.Manager, actorsByExecutorId);
                    if (actor.IsManager) break;
                    if (serializedTurns)
                    {
                        foreach (var previousState in states.Values.Where(value => value.Active is not null && value.Participant.Id != actor.Id).OrderBy(value => value.Active!.Turn))
                        {
                            var previousTurn = previousState.CompleteActive();
                            if (!string.IsNullOrEmpty(previousTurn.Delta))
                                yield return new FlowParticipantDelta(previousState.Participant.Id, previousTurn.Delta);
                            if (previousTurn.Include)
                                yield return new FlowParticipantTurnCompleted(previousState.Participant.Id, previousTurn.Turn.Turn);
                        }
                    }
                    state = states[actor.Id];
                    responseId = ResponseKey(response.Response.ResponseId, null, state.Active?.ResponseId);
                    if (state.IsCompleted(responseId))
                    {
                        state.MergeUsage(response.Response.Usage);
                        break;
                    }
                    if (state.Active is null)
                    {
                        state.Start(++nextTurn, responseId);
                        yield return new FlowParticipantTurnStarted(actor.Id, nextTurn);
                    }
                    else
                    {
                        state.Correlate(responseId);
                    }
                    state.Capture(response.Response.Messages.SelectMany(message => message.Contents));
                    if (state.Active!.Content.Length == 0 && !string.IsNullOrEmpty(response.Response.Text))
                    {
                        var delta = state.Append(response.Response.Text);
                        if (!string.IsNullOrEmpty(delta))
                            yield return new FlowParticipantDelta(actor.Id, delta);
                    }
                    state.MergeUsage(response.Response.Usage);
                    var responseTurn = state.CompleteActive();
                    if (!string.IsNullOrEmpty(responseTurn.Delta))
                        yield return new FlowParticipantDelta(actor.Id, responseTurn.Delta);
                    if (responseTurn.Include)
                        yield return new FlowParticipantTurnCompleted(actor.Id, responseTurn.Turn.Turn);
                    break;

                case WorkflowOutputEvent completed when !completed.IsIntermediate() && completed.Is<List<ChatMessage>>():
                    finalMessages = completed.As<List<ChatMessage>>();
                    break;
                case WorkflowOutputEvent completed when !completed.IsIntermediate():
                    unsupportedOutputTypes.Add(completed.Data?.GetType().FullName ?? "<null>");
                    break;
            }
        }

        foreach (var state in states.Values.Where(value => value.Active is not null).OrderBy(value => value.Active!.Turn))
        {
            var completed = state.CompleteActive();
            if (!string.IsNullOrEmpty(completed.Delta))
                yield return new FlowParticipantDelta(state.Participant.Id, completed.Delta);
            if (completed.Include)
                yield return new FlowParticipantTurnCompleted(state.Participant.Id, completed.Turn.Turn);
        }

        var participantResults = request.Definition.Participants
            .Select(participant => states[participant.Id])
            .Where(state => state.Turns.Count > 0)
            .Select(state => state.ToResult())
            .ToArray();
        foreach (var participantResult in participantResults)
            yield return new FlowParticipantCompleted(participantResult);

        if (finalMessages is null && participantResults.Length == 0)
            throw new FlowValidationException(
                "flow_orchestration_output_invalid",
                unsupportedOutputTypes.Count == 0
                    ? "The orchestration returned no supported terminal output."
                    : $"The orchestration returned unsupported terminal output: {string.Join(", ", unsupportedOutputTypes)}.");

        var finalOutput = SelectFinalOutput(request.Definition.Strategy, finalMessages ?? [], participantResults, terminationPhrase);
        yield return new FlowExecutionCompleted(new FlowOrchestrationResult(
            request.Definition.Strategy,
            finalOutput,
            participantResults));
    }

    private async Task<IReadOnlyDictionary<string, ResolvedParticipant>> ResolveParticipantsAsync(
        IReadOnlyList<FlowTargetReference> references,
        IReadOnlyList<RuntimeExecutionBinding>? persistedBindings,
        ToolExecutionScope executionScope,
        CancellationToken cancellationToken)
    {
        var participants = new Dictionary<string, ResolvedParticipant>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var @namespace = reference.Namespace ?? Agentstration.Resources.ResourceNamespace.Default;
            var binding = persistedBindings?.SingleOrDefault(value => string.Equals(value.ParticipantId, reference.Id, StringComparison.Ordinal));
            var resolved = binding is null
                ? await agentResolver.ResolveLatestAsync(reference.Id, @namespace, cancellationToken)
                : await agentResolver.ResolveAsync(new RuntimeAgentReference(binding.AgentResourceId, binding.AgentGeneration)
                {
                    Namespace = binding.AgentNamespace
                }, cancellationToken);
            if (binding is null && !resolved.Ready)
                throw new FlowValidationException("flow_participant_not_ready", $"Agent '{reference.Id}' is not ready: {resolved.Error ?? resolved.State}.");
            if (binding is not null && (resolved.Generation != binding.AgentGeneration
                || !string.Equals(resolved.RevisionId, binding.RevisionId, StringComparison.Ordinal)
                || !string.Equals(resolved.DeploymentId, binding.DeploymentId, StringComparison.Ordinal)))
                throw new FlowValidationException("flow_runtime_binding_mismatch", $"The exact runtime binding for participant '{reference.Id}' is no longer available.");
            if (!string.Equals(resolved.Definition.Handler, agentFactory.Handler, StringComparison.Ordinal))
                throw new FlowValidationException("flow_participant_handler_unsupported", $"Agent '{reference.Id}' does not use the supported runtime handler.");
            var agent = await agentFactory.CreateAgentAsync(
                resolved.Definition,
                resolved.RevisionId,
                resolved.Generation,
                new AgentRuntimeContext(tools, toolExecution),
                executionScope,
                cancellationToken);
            var effectiveBinding = binding ?? new RuntimeExecutionBinding
            {
                ParticipantId = reference.Id,
                AgentNamespace = @namespace,
                AgentResourceId = resolved.AgentName,
                AgentGeneration = resolved.Generation,
                DeploymentId = resolved.DeploymentId,
                RevisionId = resolved.RevisionId,
                RuntimeProfileName = resolved.RuntimeProfileName,
                RuntimeProfileNamespace = resolved.RuntimeProfileNamespace,
                ModelProfileName = resolved.ModelProfileName
            };
            participants.Add(reference.Id, new ResolvedParticipant(reference.Id, resolved, agent, effectiveBinding));
        }
        return participants;
    }

    private static ToolExecutionScope ToolScope(FlowOrchestrationExecutionRequest request) => request.Scope is null
        ? new ToolExecutionScope
        {
            OwnerKind = ToolExecutionOwnerKind.FlowRun,
            WorkspaceId = request.WorkspaceId,
            ExecutionId = request.RunId,
            CorrelationId = request.CorrelationId
        }
        : new ToolExecutionScope
        {
            OwnerKind = ToolExecutionOwnerKind.FlowRun,
            TenantId = request.Scope.TenantId,
            WorkspaceId = request.Scope.WorkspaceId,
            PrincipalId = request.Scope.PrincipalId,
            ExecutionId = request.RunId,
            CorrelationId = request.CorrelationId
        };

    private async Task<BuiltWorkflow> BuildWorkflowAsync(
        OrchestrationFlowDefinition definition,
        IReadOnlyDictionary<string, ResolvedParticipant> participants,
        CancellationToken cancellationToken)
    {
        switch (definition.Pattern)
        {
            case SequentialOrchestrationPattern sequential:
                return new(AgentWorkflowBuilder.BuildSequential(!sequential.IncludeFullHistory, Ordered(definition, participants)), null);
            case ConcurrentOrchestrationPattern:
                return new(AgentWorkflowBuilder.BuildConcurrent(Ordered(definition, participants)), null);
            case HandoffOrchestrationPattern handoff:
                var handoffBuilder = AgentWorkflowBuilder.CreateHandoffBuilderWith(participants[handoff.InitialParticipant].Agent);
                // Streaming updates already carry text, function calls, finish reasons, and usage. Emitting the
                // aggregated response as well would report the same agent invocation twice.
                handoffBuilder.EmitAgentResponseUpdateEvents(true).EmitAgentResponseEvents(false);
                foreach (var routes in handoff.Handoffs.GroupBy(route => route.From, StringComparer.Ordinal))
                    handoffBuilder.WithHandoffs(participants[routes.Key].Agent, routes.Select(route => participants[route.To].Agent));
                if (!string.IsNullOrWhiteSpace(handoff.TerminationPhrase))
                {
                    handoffBuilder.WithHandoffInstructions($"""
                        Use a handoff tool when another participant should take over the conversation.
                        When the user's request is fully resolved and no handoff is needed, end the final answer with exactly {handoff.TerminationPhrase}.
                        Never emit {handoff.TerminationPhrase} when performing a handoff.
                        """);
                    handoffBuilder.WithTerminationCondition(history => history.Any(message =>
                        message.Text?.Contains(handoff.TerminationPhrase, StringComparison.OrdinalIgnoreCase) == true));
                }
                if (handoff.Autonomous)
                {
                    if (string.IsNullOrWhiteSpace(handoff.TerminationPhrase))
                    {
                        handoffBuilder.WithAutonomousMode(handoff.MaximumTurnsPerParticipant);
                    }
                    else
                    {
                        handoffBuilder.WithAutonomousMode(
                            handoff.MaximumTurnsPerParticipant,
                            $"If your previous response fully resolved the user's request, reply only with {handoff.TerminationPhrase}. Otherwise, continue assisting or use a handoff tool when another participant should take over.");
                    }
                }
                return new(handoffBuilder.Build(), null);
            case GroupChatOrchestrationPattern groupChat:
                return new(AgentWorkflowBuilder.CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents)
                {
                    MaximumIterationCount = groupChat.MaximumIterations
                })
                    .AddParticipants(Ordered(definition, participants))
                    .Build(), null);
            case MagenticOrchestrationPattern magentic:
                var manager = participants[magentic.Manager.Id];
                if (manager.Resolution.Definition.EffectiveToolNames.Count > 0)
                    throw new FlowValidationException(
                        "flow_magentic_manager_tools_unsupported",
                        $"Magentic manager '{magentic.Manager.Id}' cannot declare tools.");
                var workflow = new MagenticWorkflowBuilder(manager.Agent)
                    .AddParticipants(Ordered(definition, participants))
                    .WithMaxRounds(magentic.MaximumRounds)
                    .WithMaxStalls(magentic.MaximumStalls)
                    .WithMaxResets(magentic.MaximumResets)
                    .RequirePlanSignoff(false)
                    .Build();
                return new(workflow, manager);
            default:
                throw new FlowValidationException("flow_orchestration_strategy_unsupported", "The orchestration strategy is not supported.");
        }
    }

    private static IEnumerable<AIAgent> Ordered(
        OrchestrationFlowDefinition definition,
        IReadOnlyDictionary<string, ResolvedParticipant> participants) =>
        definition.Participants.Select(participant => participants[participant.Id].Agent);

    internal static string ResolveParticipantId(
        string? agentId,
        string executorId,
        IReadOnlyDictionary<string, AIAgent> participants,
        IReadOnlyDictionary<string, string>? participantByExecutorId = null)
    {
        if (participantByExecutorId?.TryGetValue(executorId, out var mappedParticipantId) == true)
            return mappedParticipantId;
        foreach (var participant in participants)
            if (string.Equals(participant.Value.Id, agentId, StringComparison.Ordinal)
                || string.Equals(participant.Value.Id, executorId, StringComparison.Ordinal))
                return participant.Key;
        throw new FlowValidationException(
            "flow_orchestration_participant_unmapped",
            $"The orchestration emitted an event for an unknown participant (agent '{agentId ?? "<null>"}', executor '{executorId}').");
    }

    private static WorkflowActor ResolveActor(
        string? agentId,
        string executorId,
        IReadOnlyDictionary<string, ResolvedParticipant> participants,
        ResolvedParticipant? manager,
        IReadOnlyDictionary<string, WorkflowActor> actorsByExecutorId)
    {
        if (actorsByExecutorId.TryGetValue(executorId, out var actor)) return actor;
        foreach (var participant in participants.Values)
            if (string.Equals(participant.Agent.Id, agentId, StringComparison.Ordinal)
                || string.Equals(participant.Agent.Id, executorId, StringComparison.Ordinal))
                return new(participant.Id, false);
        var executorMatches = participants.Values
            .Where(participant => executorId.StartsWith($"{NormalizeExecutorId(participant.Id)}_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (executorMatches.Length == 1) return new(executorMatches[0].Id, false);
        if (manager is not null
            && (string.Equals(manager.Agent.Id, agentId, StringComparison.Ordinal)
                || string.Equals(manager.Agent.Id, executorId, StringComparison.Ordinal)))
            return new(manager.Id, true);
        throw new FlowValidationException(
            "flow_orchestration_participant_unmapped",
            $"The orchestration emitted an event for an unknown participant (agent '{agentId ?? "<null>"}', executor '{executorId}').");
    }

    internal static ExternalResponse CreateResponse(ExternalRequest request, InputRequest input)
    {
        var value = input.Response!.Value;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        if (request.TryGetDataAs<IExternalRequestEnvelope>(out var envelope) && envelope is not null)
        {
            var inner = envelope.GetInnerRequestContent();
            AIContent? approvalResponse = value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && inner is ToolApprovalRequestContent approval
                    ? approval.CreateResponse(value.GetBoolean())
                    : null;
            IList<ChatMessage> messages = approvalResponse is null
                ? [new ChatMessage(ChatRole.User, text)]
                : [new ChatMessage(ChatRole.User, [approvalResponse])];
            return request.CreateResponse(envelope.CreateResponse(messages));
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && request.TryGetDataAs<ToolApprovalRequestContent>(out var directApproval)
            && directApproval is not null)
            return request.CreateResponse(directApproval.CreateResponse(value.GetBoolean()));
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return request.CreateResponse(value.GetBoolean());
        if (request.IsDataOfType<ChatMessage>())
            return request.CreateResponse(new ChatMessage(ChatRole.User, text));
        if (request.IsDataOfType<List<ChatMessage>>())
            return request.CreateResponse(new List<ChatMessage> { new(ChatRole.User, text) });
        return request.CreateResponse(text);
    }

    internal static InteractionDescription DescribeInteraction(ExternalRequest request, string? participantPrompt)
    {
        if (request.TryGetDataAs<ToolApprovalRequestContent>(out var approval) && approval is not null)
            return new(InputRequestType.Confirmation, "Approve the requested tool operation?");
        if (request.TryGetDataAs<IExternalRequestEnvelope>(out var envelope)
            && envelope?.GetInnerRequestContent() is ToolApprovalRequestContent)
            return new(InputRequestType.Confirmation, "Approve the requested tool operation?");
        return new(
            InputRequestType.Text,
            string.IsNullOrWhiteSpace(participantPrompt)
                ? "Additional input is required to continue this execution."
                : participantPrompt);
    }

    private static string NormalizeExecutorId(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

    private static IReadOnlyDictionary<string, WorkflowActor> MapExecutors(
        Workflow workflow,
        IReadOnlyDictionary<string, ResolvedParticipant> participants,
        ResolvedParticipant? manager)
    {
        var actorsByAgent = new Dictionary<AIAgent, WorkflowActor>(ReferenceEqualityComparer.Instance);
        foreach (var participant in participants.Values)
            actorsByAgent.Add(participant.Agent, new(participant.Id, manager?.Id == participant.Id));
        var result = new Dictionary<string, WorkflowActor>(StringComparer.Ordinal);
        foreach (var binding in workflow.ReflectExecutors())
            if (binding.Value.RawValue is AIAgent agent && actorsByAgent.TryGetValue(agent, out var actor))
                result.Add(binding.Key, actor);
        return result;
    }

    private static JsonElement SelectFinalOutput(
        FlowOrchestrationStrategy strategy,
        IReadOnlyList<ChatMessage> finalMessages,
        IReadOnlyList<FlowParticipantResult> participants,
        string? terminationPhrase)
    {
        if (strategy == FlowOrchestrationStrategy.Concurrent)
            return JsonSerializer.SerializeToElement(participants.Select(result => new
            {
                result.ParticipantId,
                Output = result.Output
            }).ToArray(), JsonOptions);
        var text = finalMessages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => VisibleHandoffText(message.Text, terminationPhrase))
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? participants
                .SelectMany(participant => participant.Turns)
                .Select(turn => VisibleHandoffText(turn.Content, terminationPhrase))
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        return JsonSerializer.SerializeToElement(text);
    }

    private static string VisibleHandoffText(string? text, string? terminationPhrase)
    {
        text ??= string.Empty;
        return string.IsNullOrWhiteSpace(terminationPhrase)
            ? text
            : text.Replace(terminationPhrase, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static bool IsInternalOrchestrationTool(string name) =>
        name.StartsWith("handoff_to_", StringComparison.Ordinal);

    private static string ResponseKey(string? responseId, string? messageId, string? activeResponseId) =>
        !string.IsNullOrWhiteSpace(responseId) ? responseId
        : !string.IsNullOrWhiteSpace(messageId) ? messageId
        : !string.IsNullOrWhiteSpace(activeResponseId) ? activeResponseId
        : $"response-{Guid.NewGuid():N}";

    private static string Prompt(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("prompt", out var prompt)
        && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString()!
            : input.GetRawText();

    private sealed record BuiltWorkflow(Workflow Workflow, ResolvedParticipant? Manager);
    internal sealed record InteractionDescription(InputRequestType Type, string Prompt);
    private sealed record ResolvedParticipant(
        string Id,
        ResolvedRuntimeAgent Resolution,
        AIAgent Agent,
        RuntimeExecutionBinding Binding);
    private sealed record WorkflowActor(string Id, bool IsManager);

    private sealed class ParticipantState(ResolvedParticipant participant, string? terminationPhrase)
    {
        private readonly HashSet<string> completedResponseIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> tools = new(StringComparer.Ordinal);

        public ResolvedParticipant Participant { get; } = participant;
        public MutableTurn? Active { get; private set; }
        public List<MutableTurn> Turns { get; } = [];

        public bool IsCompleted(string responseId) => completedResponseIds.Contains(responseId);

        public void Start(int turn, string responseId) => Active = new MutableTurn(turn, responseId, terminationPhrase);

        public void Correlate(string responseId) => Active!.ResponseIds.Add(responseId);

        public string Append(string content) => Active!.Append(content);

        public void Capture(IEnumerable<AIContent> contents)
        {
            foreach (var content in contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    if (!IsInternalOrchestrationTool(functionCall.Name))
                        tools.Add(functionCall.Name);
                }
                if (content is UsageContent usage) MergeUsage(usage.Details);
            }
        }

        public void MergeUsage(UsageDetails? usage)
        {
            if (usage is null || Active is null) return;
            Active.InputTokens = Math.Max(Active.InputTokens ?? 0, usage.InputTokenCount ?? 0);
            Active.OutputTokens = Math.Max(Active.OutputTokens ?? 0, usage.OutputTokenCount ?? 0);
        }

        public CompletedTurn CompleteActive()
        {
            var completed = Active ?? throw new InvalidOperationException("No participant turn is active.");
            Active = null;
            var delta = completed.Flush();
            completedResponseIds.UnionWith(completed.ResponseIds);
            var duplicateEmptyTurn = completed.Content.Length == 0
                && Turns.LastOrDefault()?.Content.Length == 0;
            var include = !duplicateEmptyTurn && !completed.IsTerminationMarkerOnly;
            if (include) Turns.Add(completed);
            return new(completed, delta, include);
        }

        public FlowParticipantResult ToResult()
        {
            var turns = Turns.Select(turn => new FlowParticipantTurnResult(turn.Turn, turn.Content.ToString())).ToArray();
            var inputTokens = Turns.Sum(turn => turn.InputTokens ?? 0);
            var outputTokens = Turns.Sum(turn => turn.OutputTokens ?? 0);
            var usage = inputTokens == 0 && outputTokens == 0
                ? null
                : new FlowStepRunUsage(ToInt32(inputTokens), ToInt32(outputTokens));
            return new FlowParticipantResult(
                Participant.Id,
                turns,
                JsonSerializer.SerializeToElement(turns.LastOrDefault()?.Content ?? string.Empty),
                Participant.Resolution.AgentName,
                Participant.Resolution.Definition.AgentVersion,
                Participant.Resolution.ModelProfileName,
                null,
                tools.Union(Participant.Resolution.Definition.EffectiveToolNames, StringComparer.Ordinal).Order().ToArray(),
                usage);
        }

        private static int ToInt32(long value) => value > int.MaxValue ? int.MaxValue : checked((int)value);
    }

    private sealed record CompletedTurn(MutableTurn Turn, string Delta, bool Include);

    private sealed class MutableTurn(int turn, string responseId, string? terminationPhrase)
    {
        private readonly TerminationPhraseFilter? terminationFilter = string.IsNullOrWhiteSpace(terminationPhrase)
            ? null
            : new(terminationPhrase);

        public int Turn { get; } = turn;
        public string ResponseId { get; } = responseId;
        public HashSet<string> ResponseIds { get; } = new(StringComparer.Ordinal) { responseId };
        public StringBuilder Content { get; } = new();
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public bool IsTerminationMarkerOnly => terminationFilter?.Matched == true && Content.Length == 0;

        public string Append(string content)
        {
            var visible = terminationFilter?.Append(content) ?? content;
            Content.Append(visible);
            return visible;
        }

        public string Flush()
        {
            var visible = terminationFilter?.Flush() ?? string.Empty;
            Content.Append(visible);
            return visible;
        }
    }

    internal sealed class TerminationPhraseFilter(string phrase)
    {
        private readonly StringBuilder pending = new();
        public bool Matched { get; private set; }

        public string Append(string content)
        {
            pending.Append(content);
            var visible = new StringBuilder();
            while (pending.Length > 0)
            {
                var value = pending.ToString();
                var marker = value.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    Matched = true;
                    visible.Append(value.AsSpan(0, marker));
                    pending.Clear().Append(value.AsSpan(marker + phrase.Length));
                    continue;
                }

                var retained = RetainedSuffixLength(value);
                visible.Append(value.AsSpan(0, value.Length - retained));
                pending.Clear().Append(value.AsSpan(value.Length - retained));
                break;
            }
            return visible.ToString();
        }

        public string Flush()
        {
            var visible = pending.ToString();
            pending.Clear();
            return visible;
        }

        private int RetainedSuffixLength(string value)
        {
            var maximum = Math.Min(value.Length, phrase.Length - 1);
            for (var length = maximum; length > 0; length--)
                if (value.AsSpan(value.Length - length).Equals(phrase.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                    return length;
            return 0;
        }
    }
}
#pragma warning restore MAAIW001
