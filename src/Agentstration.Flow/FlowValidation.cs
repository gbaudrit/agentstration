using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flow;

public static class FlowRunStatusExtensions
{
    public static bool IsTerminal(this FlowRunStatus status) => status is FlowRunStatus.Succeeded or FlowRunStatus.Failed or FlowRunStatus.Cancelled or FlowRunStatus.TimedOut;
}

public sealed class FlowValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public static class FlowValidator
{
    public const int MaximumOrchestrationParticipants = 16;
    public const int MaximumGroupChatIterations = 100;
    public const int MaximumHandoffTurnsPerParticipant = 50;
    public const int MaximumMagenticRounds = 50;
    public const int MaximumMagenticStalls = 10;
    public const int MaximumMagenticResets = 5;
    public const int MaximumTerminationPhraseLength = 256;

    public static void Validate(FlowResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateName(resource.Id.Value, "flow_id_invalid");
        ValidateName(resource.Name, "flow_name_invalid");
        if (!string.Equals(resource.Id.Value, resource.Name, StringComparison.Ordinal))
            throw new FlowValidationException("flow_identity_mismatch", "Flow id and name must match.");
        ValidateVersion(resource.Version);
        if (resource.ActiveVersion is not null) ValidateVersion(resource.ActiveVersion);
        ValidateDefinition(resource.Id, resource.Definition);
    }

    public static void ValidateVersion(FlowVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        ValidateName(version.FlowId.Value, "flow_id_invalid");
        ValidateVersion(version.Version);
        ValidateDefinition(version.FlowId, version.Definition);
    }

    public static void ValidateReference(FlowReference reference)
    {
        ValidateName(reference.FlowId.Value, "flow_reference_invalid");
        if (reference.Version is not null) ValidateVersion(reference.Version);
        if (reference.Version is null && !reference.UseActiveVersion)
            throw new FlowValidationException("flow_reference_unresolved", "A Flow reference must select a version or the active version.");
        if (reference.Version is not null && reference.UseActiveVersion)
            throw new FlowValidationException("flow_reference_ambiguous", "A Flow reference cannot select both an exact and active version.");
    }

    private static void ValidateDefinition(FlowId flowId, FlowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        switch (definition)
        {
            case DirectFlowDefinition direct:
                ValidateTarget(direct.Target);
                if (direct.Target?.Kind == FlowTargetKind.Flow) throw new FlowValidationException("direct_target_invalid", "A Direct Flow must target an Agent.");
                break;
            case RoutingFlowDefinition routing:
                if (routing.Destinations is null || routing.Destinations.Count == 0) throw new FlowValidationException("routing_destinations_required", "A Routing Flow requires at least one destination.");
                foreach (var target in routing.Destinations) ValidateTarget(target);
                if (routing.Fallback is not null) ValidateTarget(routing.Fallback);
                break;
            case WorkflowFlowDefinition workflow:
                ValidateWorkflow(workflow);
                break;
            case OrchestrationFlowDefinition orchestration:
                if (orchestration.Participants is null || orchestration.Participants.Count == 0) throw new FlowValidationException("orchestration_participants_required", "An Orchestration Flow requires at least one participant.");
                foreach (var participant in orchestration.Participants)
                {
                    ValidateTarget(participant);
                    if (participant.Kind == FlowTargetKind.Flow) throw new FlowValidationException("orchestration_participant_invalid", "Orchestration participants must be Agents.");
                }
                if (orchestration.Participants.Select(participant => participant.Id).Distinct(StringComparer.Ordinal).Count() != orchestration.Participants.Count)
                    throw new FlowValidationException("orchestration_participant_duplicate", "Orchestration participant identifiers must be unique.");
                if (orchestration.Participants.Count < 2)
                    throw new FlowValidationException("orchestration_participants_insufficient", "A built-in orchestration requires at least two participants.");
                if (orchestration.Participants.Count > MaximumOrchestrationParticipants)
                    throw new FlowValidationException("orchestration_participants_limit_exceeded", $"An orchestration supports at most {MaximumOrchestrationParticipants} participants.");
                ValidateOrchestration(orchestration);
                break;
            case CompositeFlowDefinition composite:
                if (composite.Flows is null || composite.Flows.Count == 0) throw new FlowValidationException("composite_flows_required", "A Composite Flow requires at least one child Flow.");
                foreach (var child in composite.Flows)
                {
                    ValidateReference(child);
                    if (child.FlowId == flowId) throw new FlowValidationException("composite_self_reference", "A Composite Flow cannot directly reference itself.");
                }
                break;
            default:
                throw new FlowValidationException("flow_definition_unknown", "The Flow definition type is not supported.");
        }
    }

    private static void ValidateOrchestration(OrchestrationFlowDefinition orchestration)
    {
        ArgumentNullException.ThrowIfNull(orchestration.Pattern);
        var participantIds = orchestration.Participants.Select(participant => participant.Id).ToHashSet(StringComparer.Ordinal);
        switch (orchestration.Pattern)
        {
            case SequentialOrchestrationPattern:
            case ConcurrentOrchestrationPattern:
                break;
            case HandoffOrchestrationPattern handoff:
                if (!participantIds.Contains(handoff.InitialParticipant))
                    throw new FlowValidationException("handoff_initial_participant_unknown", "The initial Handoff participant must belong to the orchestration.");
                if (handoff.MaximumTurnsPerParticipant is < 1 or > MaximumHandoffTurnsPerParticipant)
                    throw new FlowValidationException("handoff_turn_limit_invalid", $"The Handoff turn limit must be between 1 and {MaximumHandoffTurnsPerParticipant}.");
                if (handoff.Handoffs is null || handoff.Handoffs.Count == 0)
                    throw new FlowValidationException("handoff_routes_required", "A Handoff orchestration requires at least one route.");
                if (handoff.Handoffs.Any(route => route is null || !participantIds.Contains(route.From) || !participantIds.Contains(route.To) || route.From == route.To))
                    throw new FlowValidationException("handoff_route_invalid", "Handoff routes must connect two distinct orchestration participants.");
                if (handoff.Handoffs.Select(route => (route.From, route.To)).Distinct().Count() != handoff.Handoffs.Count)
                    throw new FlowValidationException("handoff_route_duplicate", "Handoff routes must be unique.");
                var reachable = ReachableParticipants(handoff.InitialParticipant, handoff.Handoffs);
                if (participantIds.Except(reachable, StringComparer.Ordinal).Any())
                    throw new FlowValidationException("handoff_participant_unreachable", "Every Handoff participant must be reachable from the initial participant.");
                if (handoff.TerminationPhrase?.Length > MaximumTerminationPhraseLength)
                    throw new FlowValidationException("handoff_termination_phrase_too_long", $"The Handoff termination phrase cannot exceed {MaximumTerminationPhraseLength} characters.");
                break;
            case GroupChatOrchestrationPattern groupChat when groupChat.MaximumIterations is < 1 or > MaximumGroupChatIterations:
                throw new FlowValidationException("group_chat_iterations_invalid", $"The Group Chat iteration limit must be between 1 and {MaximumGroupChatIterations}.");
            case GroupChatOrchestrationPattern:
                break;
            case MagenticOrchestrationPattern magentic:
                ValidateTarget(magentic.Manager);
                if (magentic.Manager is null || magentic.Manager.Kind != FlowTargetKind.Agent)
                    throw new FlowValidationException("magentic_manager_invalid", "The Magentic manager must reference an Agent.");
                if (participantIds.Contains(magentic.Manager.Id))
                    throw new FlowValidationException("magentic_manager_participant", "The Magentic manager must be distinct from its participants.");
                if (magentic.MaximumRounds is < 1 or > MaximumMagenticRounds
                    || magentic.MaximumStalls is < 1 or > MaximumMagenticStalls
                    || magentic.MaximumResets is < 0 or > MaximumMagenticResets)
                    throw new FlowValidationException("magentic_limits_invalid", $"Magentic limits are rounds 1..{MaximumMagenticRounds}, stalls 1..{MaximumMagenticStalls}, and resets 0..{MaximumMagenticResets}.");
                break;
            default:
                throw new FlowValidationException("orchestration_pattern_unknown", "The orchestration pattern is not supported.");
        }
    }

    private static IReadOnlySet<string> ReachableParticipants(string initialParticipant, IReadOnlyList<FlowHandoff> handoffs)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal) { initialParticipant };
        var pending = new Queue<string>();
        pending.Enqueue(initialParticipant);
        while (pending.TryDequeue(out var current))
        {
            foreach (var destination in handoffs.Where(route => route.From == current).Select(route => route.To))
                if (reachable.Add(destination)) pending.Enqueue(destination);
        }
        return reachable;
    }

    private static void ValidateWorkflow(WorkflowFlowDefinition workflow)
    {
        if (workflow.Nodes is null || workflow.Nodes.Count == 0) throw new FlowValidationException("workflow_nodes_required", "A Workflow requires at least one node.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in workflow.Nodes)
        {
            ValidateName(node.Id, "workflow_node_id_invalid");
            if (!ids.Add(node.Id)) throw new FlowValidationException("workflow_node_duplicate", $"Workflow node '{node.Id}' is duplicated.");
            if (node.Target is not null) ValidateTarget(node.Target);
        }
        if (!ids.Contains(workflow.EntryNodeId)) throw new FlowValidationException("workflow_entry_unknown", "The Workflow entry node does not exist.");
        foreach (var edge in workflow.Edges ?? [])
        {
            if (!ids.Contains(edge.Source) || !ids.Contains(edge.Destination))
                throw new FlowValidationException("workflow_edge_invalid", $"Workflow edge '{edge.Source}' -> '{edge.Destination}' references an unknown node.");
        }
        if (workflow.OutputNodeIds is not null && workflow.OutputNodeIds.Any(output => !ids.Contains(output)))
            throw new FlowValidationException("workflow_output_unknown", "A Workflow output node does not exist.");
    }

    private static void ValidateTarget(FlowTargetReference? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Id)) throw new FlowValidationException("flow_target_required", "A Flow target identifier is required.");
        if (target.Kind != FlowTargetKind.Flow && target.Version is not null)
            throw new FlowValidationException("flow_target_version_invalid", "Only a Flow target can select a Flow version.");
        if (target.Version is not null) ValidateVersion(target.Version);
    }

    private static void ValidateName(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsLetterOrDigit(value[0]) || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new FlowValidationException(code, "Identifiers must contain 1 to 128 letters, digits, '-' or '_' and start with a letter or digit.");
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new FlowValidationException("flow_version_invalid", "A Flow version is required.");
        var versionParts = version.Split('-', 2, StringSplitOptions.TrimEntries);
        if (versionParts.Length == 2 && (string.IsNullOrWhiteSpace(versionParts[1]) || versionParts[1].Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-')))
            throw new FlowValidationException("flow_version_invalid", "The semantic version prerelease suffix is invalid.");
        var main = versionParts[0].Split('.', StringSplitOptions.TrimEntries);
        if (main.Length != 3 || main.Any(part => !int.TryParse(part, out var value) || value < 0))
            throw new FlowValidationException("flow_version_invalid", "Flow versions must use semantic version form 'major.minor.patch'.");
    }
}
