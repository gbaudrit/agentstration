using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Flow;

public readonly record struct FlowId(string Value)
{
    public override string ToString() => Value;
}

public sealed record FlowReference(FlowId FlowId, string? Version = null, bool UseActiveVersion = true);

public enum FlowKind { Direct, Routing, Workflow, Orchestration, Composite }
public enum FlowTargetKind { Agent, AgentType, Flow }
public enum FlowRoutingStrategy { Deterministic, Capabilities, Semantic, Llm, Hybrid, Custom }
public enum FlowNodeKind { Agent, Flow, Function, ExternalCall, HumanApproval, Custom }
public enum FlowOrchestrationStrategy { Sequential, Concurrent, Handoff, GroupChat, Magentic, Custom }
public enum FlowCompositionMode { Sequential, Concurrent, Custom }

public sealed record FlowTargetReference(FlowTargetKind Kind, string Id, string? Version = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "specKind")]
[JsonDerivedType(typeof(DirectFlowSpec), "direct")]
[JsonDerivedType(typeof(RoutingFlowSpec), "routing")]
[JsonDerivedType(typeof(WorkflowFlowSpec), "workflow")]
[JsonDerivedType(typeof(OrchestrationFlowSpec), "orchestration")]
[JsonDerivedType(typeof(CompositeFlowSpec), "composite")]
public abstract record FlowSpec;

public sealed record DirectFlowSpec(FlowTargetReference Target) : FlowSpec;
public sealed record RoutingFlowSpec(
    FlowRoutingStrategy Strategy,
    IReadOnlyList<FlowTargetReference> Destinations,
    FlowTargetReference? Fallback = null,
    IReadOnlyDictionary<string, JsonElement>? Configuration = null) : FlowSpec;
public sealed record FlowNode(
    string Id,
    FlowNodeKind Kind,
    FlowTargetReference? Target = null,
    IReadOnlyDictionary<string, JsonElement>? Configuration = null);
public sealed record FlowEdge(string Source, string Destination, string? Condition = null);
public sealed record WorkflowFlowSpec(
    string EntryNodeId,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string>? OutputNodeIds = null) : FlowSpec;
public sealed record OrchestrationFlowSpec(
    FlowOrchestrationStrategy Strategy,
    IReadOnlyList<FlowTargetReference> Participants,
    int? MaximumIterations = null,
    string? TerminationStrategy = null,
    IReadOnlyDictionary<string, JsonElement>? Configuration = null) : FlowSpec;
public sealed record CompositeFlowSpec(
    FlowCompositionMode Mode,
    IReadOnlyList<FlowReference> Flows,
    IReadOnlyDictionary<string, JsonElement>? Configuration = null) : FlowSpec;

public sealed record FlowDefinition(
    FlowId Id,
    string Name,
    string? Description,
    FlowKind Kind,
    string Version,
    bool Enabled,
    string? ActiveVersion,
    FlowSpec Spec,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FlowVersion(
    FlowId FlowId,
    string Version,
    string? Description,
    FlowKind Kind,
    FlowSpec Spec,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset PublishedAt);

public sealed class FlowValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public static class FlowValidator
{
    public static void Validate(FlowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Id.Value, "flow_id_invalid");
        ValidateName(definition.Name, "flow_name_invalid");
        if (!string.Equals(definition.Id.Value, definition.Name, StringComparison.Ordinal))
            throw new FlowValidationException("flow_identity_mismatch", "Flow id and name must match.");
        ValidateVersion(definition.Version);
        if (definition.ActiveVersion is not null) ValidateVersion(definition.ActiveVersion);
        ValidateSpec(definition.Id, definition.Kind, definition.Spec);
    }

    public static void ValidateVersion(FlowVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        ValidateName(version.FlowId.Value, "flow_id_invalid");
        ValidateVersion(version.Version);
        ValidateSpec(version.FlowId, version.Kind, version.Spec);
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

    private static void ValidateSpec(FlowId flowId, FlowKind kind, FlowSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var actualKind = spec switch
        {
            DirectFlowSpec => FlowKind.Direct,
            RoutingFlowSpec => FlowKind.Routing,
            WorkflowFlowSpec => FlowKind.Workflow,
            OrchestrationFlowSpec => FlowKind.Orchestration,
            CompositeFlowSpec => FlowKind.Composite,
            _ => throw new FlowValidationException("flow_spec_unknown", "The Flow specification type is not supported.")
        };
        if (kind != actualKind) throw new FlowValidationException("flow_spec_kind_mismatch", $"Flow kind '{kind}' does not match specification '{actualKind}'.");

        switch (spec)
        {
            case DirectFlowSpec direct:
                ValidateTarget(direct.Target);
                if (direct.Target?.Kind == FlowTargetKind.Flow) throw new FlowValidationException("direct_target_invalid", "A Direct Flow must target an Agent or AgentType.");
                break;
            case RoutingFlowSpec routing:
                if (routing.Destinations is null || routing.Destinations.Count == 0) throw new FlowValidationException("routing_destinations_required", "A Routing Flow requires at least one destination.");
                foreach (var target in routing.Destinations) ValidateTarget(target);
                if (routing.Fallback is not null) ValidateTarget(routing.Fallback);
                break;
            case WorkflowFlowSpec workflow:
                ValidateWorkflow(workflow);
                break;
            case OrchestrationFlowSpec orchestration:
                if (orchestration.Participants is null || orchestration.Participants.Count == 0) throw new FlowValidationException("orchestration_participants_required", "An Orchestration Flow requires at least one participant.");
                foreach (var participant in orchestration.Participants)
                {
                    ValidateTarget(participant);
                    if (participant.Kind == FlowTargetKind.Flow) throw new FlowValidationException("orchestration_participant_invalid", "Orchestration participants must be Agents or AgentTypes.");
                }
                if (orchestration.MaximumIterations is < 1) throw new FlowValidationException("orchestration_iterations_invalid", "Maximum iterations must be positive.");
                break;
            case CompositeFlowSpec composite:
                if (composite.Flows is null || composite.Flows.Count == 0) throw new FlowValidationException("composite_flows_required", "A Composite Flow requires at least one child Flow.");
                foreach (var child in composite.Flows)
                {
                    ValidateReference(child);
                    if (child.FlowId == flowId) throw new FlowValidationException("composite_self_reference", "A Composite Flow cannot directly reference itself.");
                }
                break;
        }
    }

    private static void ValidateWorkflow(WorkflowFlowSpec workflow)
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
