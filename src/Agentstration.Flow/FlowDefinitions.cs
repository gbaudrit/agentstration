using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flow;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "flowKind")]
[JsonDerivedType(typeof(DirectFlowDefinition), "direct")]
[JsonDerivedType(typeof(RoutingFlowDefinition), "routing")]
[JsonDerivedType(typeof(WorkflowFlowDefinition), "workflow")]
[JsonDerivedType(typeof(OrchestrationFlowDefinition), "orchestration")]
[JsonDerivedType(typeof(CompositeFlowDefinition), "composite")]
public abstract record FlowDefinition
{
    [JsonIgnore]
    public abstract FlowKind Kind { get; }
}

public sealed record DirectFlowDefinition(FlowTargetReference Target) : FlowDefinition
{
    public override FlowKind Kind => FlowKind.Direct;
}

public sealed record RoutingFlowDefinition(
    FlowRoutingStrategy Strategy,
    IReadOnlyList<FlowTargetReference> Destinations,
    FlowTargetReference? Fallback = null) : FlowDefinition
{
    public override FlowKind Kind => FlowKind.Routing;
}

public sealed record FlowNode(
    string Id,
    FlowNodeKind Kind,
    FlowTargetReference? Target = null,
    IReadOnlyDictionary<string, JsonElement>? Configuration = null);
public sealed record FlowEdge(string Source, string Destination, string? Condition = null);
public sealed record WorkflowFlowDefinition(
    string EntryNodeId,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string>? OutputNodeIds = null) : FlowDefinition
{
    public override FlowKind Kind => FlowKind.Workflow;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "strategy")]
[JsonDerivedType(typeof(SequentialOrchestrationPattern), "sequential")]
[JsonDerivedType(typeof(ConcurrentOrchestrationPattern), "concurrent")]
[JsonDerivedType(typeof(HandoffOrchestrationPattern), "handoff")]
[JsonDerivedType(typeof(GroupChatOrchestrationPattern), "groupChat")]
[JsonDerivedType(typeof(MagenticOrchestrationPattern), "magentic")]
public abstract record FlowOrchestrationPattern
{
    [JsonIgnore]
    public abstract FlowOrchestrationStrategy Strategy { get; }
}

public sealed record SequentialOrchestrationPattern(bool IncludeFullHistory = true) : FlowOrchestrationPattern
{
    [JsonIgnore]
    public override FlowOrchestrationStrategy Strategy => FlowOrchestrationStrategy.Sequential;
}

public sealed record ConcurrentOrchestrationPattern : FlowOrchestrationPattern
{
    [JsonIgnore]
    public override FlowOrchestrationStrategy Strategy => FlowOrchestrationStrategy.Concurrent;
}

public sealed record FlowHandoff(string From, string To);

public sealed record HandoffOrchestrationPattern(
    string InitialParticipant,
    IReadOnlyList<FlowHandoff> Handoffs,
    bool Autonomous = false,
    int MaximumTurnsPerParticipant = 10,
    string? TerminationPhrase = null) : FlowOrchestrationPattern
{
    [JsonIgnore]
    public override FlowOrchestrationStrategy Strategy => FlowOrchestrationStrategy.Handoff;
}

public sealed record GroupChatOrchestrationPattern(
    int MaximumIterations = 5) : FlowOrchestrationPattern
{
    [JsonIgnore]
    public override FlowOrchestrationStrategy Strategy => FlowOrchestrationStrategy.GroupChat;
}

public sealed record MagenticOrchestrationPattern(
    FlowTargetReference Manager,
    int MaximumRounds = 10,
    int MaximumStalls = 3,
    int MaximumResets = 2) : FlowOrchestrationPattern
{
    [JsonIgnore]
    public override FlowOrchestrationStrategy Strategy => FlowOrchestrationStrategy.Magentic;
}

public sealed record OrchestrationFlowDefinition(
    IReadOnlyList<FlowTargetReference> Participants,
    FlowOrchestrationPattern Pattern) : FlowDefinition
{
    public override FlowKind Kind => FlowKind.Orchestration;

    [JsonIgnore]
    public FlowOrchestrationStrategy Strategy => Pattern.Strategy;
}

public sealed record CompositeFlowDefinition(
    FlowCompositionMode Mode,
    IReadOnlyList<FlowReference> Flows) : FlowDefinition
{
    public override FlowKind Kind => FlowKind.Composite;
}
