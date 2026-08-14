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
    AgentFrameworkRuntimeFactory agentFactory) : IFlowOrchestrationEngine
{
    public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var participants = await ResolveParticipantsAsync(request.Definition.Participants, cancellationToken);
        var workflow = await BuildWorkflowAsync(request.Definition, participants, cancellationToken);
        var participantByExecutorId = MapParticipantExecutors(workflow, participants);
        var messages = new List<ChatMessage> { new(ChatRole.User, Prompt(request.Input)) };
        var outputByParticipant = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var activeTurns = new Dictionary<string, (int Turn, string? ResponseId)>(StringComparer.Ordinal);
        var serializedTurns = request.Definition.Pattern is not ConcurrentOrchestrationPattern;
        var nextTurn = 0;
        List<ChatMessage>? finalMessages = null;

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            messages,
            request.RunId,
            cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            switch (workflowEvent)
            {
                case AgentResponseUpdateEvent update:
                    var participantId = ResolveParticipantId(update.Update.AgentId, update.ExecutorId, participants, participantByExecutorId);
                    if (serializedTurns
                        && !string.IsNullOrEmpty(update.Update.Text)
                        && activeTurns.Count > 0
                        && !activeTurns.ContainsKey(participantId))
                    {
                        foreach (var previousTurn in activeTurns.OrderBy(item => item.Value.Turn))
                            yield return new FlowParticipantTurnCompleted(previousTurn.Key, previousTurn.Value.Turn);
                        activeTurns.Clear();
                    }
                    if (activeTurns.TryGetValue(participantId, out var activeTurn)
                        && activeTurn.ResponseId is not null
                        && update.Update.ResponseId is not null
                        && !string.Equals(activeTurn.ResponseId, update.Update.ResponseId, StringComparison.Ordinal))
                    {
                        activeTurns.Remove(participantId);
                        yield return new FlowParticipantTurnCompleted(participantId, activeTurn.Turn);
                    }
                    if (!activeTurns.ContainsKey(participantId) && !string.IsNullOrEmpty(update.Update.Text))
                    {
                        activeTurns.Add(participantId, (++nextTurn, update.Update.ResponseId));
                        yield return new FlowParticipantTurnStarted(participantId, nextTurn);
                    }
                    if (!string.IsNullOrEmpty(update.Update.Text))
                    {
                        if (!outputByParticipant.TryGetValue(participantId, out var output))
                        {
                            output = new StringBuilder();
                            outputByParticipant.Add(participantId, output);
                        }
                        output.Append(update.Update.Text);
                        yield return new FlowParticipantDelta(participantId, update.Update.Text);
                    }
                    if (update.Update.FinishReason is not null
                        && activeTurns.Remove(participantId, out activeTurn))
                        yield return new FlowParticipantTurnCompleted(participantId, activeTurn.Turn);
                    break;
                case AgentResponseEvent response:
                    participantId = ResolveParticipantId(response.Response.AgentId, response.ExecutorId, participants, participantByExecutorId);
                    if (!activeTurns.Remove(participantId, out activeTurn))
                    {
                        activeTurn = (++nextTurn, response.Response.ResponseId);
                        yield return new FlowParticipantTurnStarted(participantId, activeTurn.Turn);
                    }
                    yield return new FlowParticipantTurnCompleted(participantId, activeTurn.Turn);
                    break;
                case WorkflowOutputEvent completed when completed.Is<List<ChatMessage>>():
                    finalMessages = completed.As<List<ChatMessage>>();
                    break;
            }
        }

        foreach (var activeTurn in activeTurns.OrderBy(item => item.Value.Turn))
            yield return new FlowParticipantTurnCompleted(activeTurn.Key, activeTurn.Value.Turn);

        foreach (var participant in outputByParticipant)
            yield return new FlowParticipantCompleted(participant.Key, JsonSerializer.SerializeToElement(participant.Value.ToString()));

        if (finalMessages is null)
            throw new FlowValidationException("flow_orchestration_output_invalid", "The orchestration returned no supported terminal output.");

        var finalText = string.Join(
            Environment.NewLine,
            finalMessages.Where(message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
                .Select(message => message.Text));
        yield return new FlowExecutionCompleted(JsonSerializer.SerializeToElement(finalText));
    }

    private async Task<IReadOnlyDictionary<string, AIAgent>> ResolveParticipantsAsync(
        IReadOnlyList<FlowTargetReference> references,
        CancellationToken cancellationToken)
    {
        var participants = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var resolved = await agentResolver.ResolveLatestAsync(reference.Id, cancellationToken);
            if (!resolved.Ready)
                throw new FlowValidationException("flow_participant_not_ready", $"Agent '{reference.Id}' is not ready: {resolved.Error ?? resolved.State}.");
            if (!string.Equals(resolved.Definition.Handler, agentFactory.Handler, StringComparison.Ordinal))
                throw new FlowValidationException("flow_participant_handler_unsupported", $"Agent '{reference.Id}' does not use the supported runtime handler.");
            participants.Add(reference.Id, await agentFactory.CreateAgentAsync(
                resolved.Definition,
                new AgentRuntimeContext(tools),
                cancellationToken));
        }
        return participants;
    }

    private async Task<Workflow> BuildWorkflowAsync(
        OrchestrationFlowDefinition definition,
        IReadOnlyDictionary<string, AIAgent> participants,
        CancellationToken cancellationToken)
    {
        switch (definition.Pattern)
        {
            case SequentialOrchestrationPattern sequential:
                return AgentWorkflowBuilder.BuildSequential(!sequential.IncludeFullHistory, Ordered(definition, participants));
            case ConcurrentOrchestrationPattern:
                return AgentWorkflowBuilder.BuildConcurrent(Ordered(definition, participants));
            case HandoffOrchestrationPattern handoff:
                var handoffBuilder = AgentWorkflowBuilder.CreateHandoffBuilderWith(participants[handoff.InitialParticipant]);
                foreach (var routes in handoff.Handoffs.GroupBy(route => route.From, StringComparer.Ordinal))
                    handoffBuilder.WithHandoffs(participants[routes.Key], routes.Select(route => participants[route.To]));
                if (handoff.Autonomous)
                    handoffBuilder.WithAutonomousMode(handoff.MaximumTurnsPerParticipant);
                if (!string.IsNullOrWhiteSpace(handoff.TerminationPhrase))
                    handoffBuilder.WithTerminationCondition(history => history.Any(message =>
                        message.Text?.Contains(handoff.TerminationPhrase, StringComparison.OrdinalIgnoreCase) == true));
                return handoffBuilder.Build();
            case GroupChatOrchestrationPattern groupChat:
                return AgentWorkflowBuilder.CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents)
                    {
                        MaximumIterationCount = groupChat.MaximumIterations
                    })
                    .AddParticipants(Ordered(definition, participants))
                    .Build();
            case MagenticOrchestrationPattern magentic:
                var manager = await ResolveParticipantsAsync([magentic.Manager], cancellationToken);
                return new MagenticWorkflowBuilder(manager[magentic.Manager.Id])
                    .AddParticipants(Ordered(definition, participants))
                    .WithMaxRounds(magentic.MaximumRounds)
                    .WithMaxStalls(magentic.MaximumStalls)
                    .WithMaxResets(magentic.MaximumResets)
                    .Build();
            default:
                throw new FlowValidationException("flow_orchestration_strategy_unsupported", "The orchestration strategy is not supported.");
        }
    }

    private static IEnumerable<AIAgent> Ordered(
        OrchestrationFlowDefinition definition,
        IReadOnlyDictionary<string, AIAgent> participants) =>
        definition.Participants.Select(participant => participants[participant.Id]);

    internal static string ResolveParticipantId(
        string? agentId,
        string executorId,
        IReadOnlyDictionary<string, AIAgent> participants,
        IReadOnlyDictionary<string, string>? participantByExecutorId = null)
    {
        if (participantByExecutorId?.TryGetValue(executorId, out var mappedParticipantId) == true)
            return mappedParticipantId;

        foreach (var participant in participants)
        {
            if (string.Equals(participant.Value.Id, agentId, StringComparison.Ordinal)
                || string.Equals(participant.Value.Id, executorId, StringComparison.Ordinal))
                return participant.Key;
        }

        throw new FlowValidationException(
            "flow_orchestration_participant_unmapped",
            "The orchestration emitted an event for an unknown participant.");
    }

    private static IReadOnlyDictionary<string, string> MapParticipantExecutors(
        Workflow workflow,
        IReadOnlyDictionary<string, AIAgent> participants)
    {
        var participantByAgent = new Dictionary<AIAgent, string>(ReferenceEqualityComparer.Instance);
        foreach (var participant in participants)
            participantByAgent.Add(participant.Value, participant.Key);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in workflow.ReflectExecutors())
        {
            if (binding.Value.RawValue is AIAgent agent
                && participantByAgent.TryGetValue(agent, out var participantId))
                result.Add(binding.Key, participantId);
        }
        return result;
    }

    private static string Prompt(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("prompt", out var prompt)
        && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString()!
            : input.GetRawText();
}
#pragma warning restore MAAIW001
