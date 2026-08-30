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

public sealed partial class AgentFrameworkFlowOrchestrationEngine
{
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
}

