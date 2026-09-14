using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flows;

public readonly record struct FlowId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => Value;
}

public sealed record FlowReference(FlowId FlowId, string? Version = null, bool UseActiveVersion = true, ResourceNamespace? Namespace = null)
{
    public FlowId Resolve(ResourceNamespace ownerNamespace) => new(FlowId.Value, Namespace ?? ownerNamespace);
}

public enum FlowKind { Direct, Routing, Workflow, Orchestration, Composite }
public enum FlowTargetKind { Agent, Flow }
public enum FlowRoutingStrategy { Deterministic, Capabilities, Semantic, Llm, Hybrid, Custom }
public enum FlowNodeKind { Input, Agent, Router, Condition, Transform, Output, Failure, Flow, Function, ExternalCall, HumanApproval, Custom }
public enum FlowOrchestrationStrategy { Sequential, Concurrent, Handoff, GroupChat, Magentic }
public enum FlowCompositionMode { Sequential, Concurrent, Custom }
public enum FlowRunStatus { Pending, Running, WaitingForInput, Succeeded, Failed, Cancelled, TimedOut, WaitingForChild }
public enum FlowRunTrigger { Manual, Api, WorkItem, Flow, Schedule, Event }
public enum FlowInvocationOrigin { Entry, Trigger, Api, Mcp, Agent, Console }
public enum FlowStepRunStatus { NotStarted, Running, Succeeded, Failed, Skipped, Cancelled }
public enum FlowRunEventType
{
    FlowRunCreated,
    FlowRunStarted,
    StepRunStarted,
    StepOutputDelta,
    StepRunCompleted,
    StepRunFailed,
    FlowRunCompleted,
    FlowRunFailed,
    FlowRunCancelled,
    FlowRunTimedOut,
    ParticipantTurnStarted,
    ParticipantTurnCompleted,
    InputRequested,
    InputReceived,
    InputExpired,
    FlowRunResumed,
    ToolCallStarted,
    ToolCallGovernanceEvaluated,
    ToolCallCompleted,
    ToolCallFailed,
    ParticipantHandoff,
    ChildFlowRunCreated,
    FlowRunWaitingForChild,
    FlowRunResumedFromChild
}

public enum InputRequestType { Text, Choice, Confirmation }
public enum InputRequestStatus { Pending, Answered, Expired, Cancelled }
