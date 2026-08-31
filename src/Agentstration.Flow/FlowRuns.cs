using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flow;

public sealed record FlowRunError(string Code, string Message, string? Details = null);
public sealed record FlowRunScope(Guid TenantId, WorkspaceId WorkspaceId, Guid PrincipalId);
public readonly record struct FlowRunKey(WorkspaceId WorkspaceId, string RunId);
public sealed record FlowRunEvent(WorkspaceId WorkspaceId, string RunId, long Sequence, FlowRunEventType Type, string? StepId, JsonElement? Payload, DateTimeOffset Timestamp);
public sealed record FlowStepRunUsage(int? InputTokens = null, int? OutputTokens = null);
public sealed record FlowParticipantTurnResult(int Turn, string Content);
public sealed record FlowParticipantResult(
    string ParticipantId,
    IReadOnlyList<FlowParticipantTurnResult> Turns,
    JsonElement Output,
    string AgentResourceId,
    long AgentVersion,
    string ModelProfileResourceId,
    string? Provider,
    IReadOnlyList<string> Tools,
    FlowStepRunUsage? Usage);
public sealed record FlowOrchestrationResult(
    FlowOrchestrationStrategy Strategy,
    JsonElement FinalOutput,
    IReadOnlyList<FlowParticipantResult> Participants);
public sealed record FlowStepRun
{
    public required string StepName { get; init; }
    public required string StepType { get; init; }
    public FlowStepRunStatus Status { get; init; } = FlowStepRunStatus.NotStarted;
    public JsonElement? DeclaredInput { get; init; }
    public JsonElement? ResolvedInput { get; init; }
    public JsonElement? Output { get; init; }
    public string? SelectedTransition { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Attempt { get; init; }
    public string? AgentResourceId { get; init; }
    public long? AgentVersion { get; init; }
    public string? ModelProfileResourceId { get; init; }
    public string? Provider { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Logs { get; init; } = [];
    public FlowStepRunUsage? Usage { get; init; }
    public FlowRunError? Error { get; init; }
}

public sealed record FlowRun
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Id { get; init; }
    public required FlowId FlowId { get; init; }
    public required string FlowVersion { get; init; }
    public FlowDefinitionState DefinitionState { get; init; } = FlowDefinitionState.Published;
    public long? DraftRevision { get; init; }
    public string? DefinitionSnapshotId { get; init; }
    public string? DefinitionHash { get; init; }
    public string? DeploymentResourceId { get; init; }
    public FlowRunStatus Status { get; init; } = FlowRunStatus.Pending;
    public FlowRunTrigger Trigger { get; init; }
    public string? StartedBy { get; init; }
    public string? CorrelationId { get; init; }
    public string? WorkItemResourceId { get; init; }
    public string? ParentFlowRunId { get; init; }
    public string? InteractionId { get; init; }
    public string? WorkTaskId { get; init; }
    public string? TriggerMessageId { get; init; }
    public required FlowRunScope Scope { get; init; }
    public required JsonElement Input { get; init; }
    public JsonElement? Output { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public FlowRunError? Error { get; init; }
    public required FlowVersion DefinitionSnapshot { get; init; }
    public IReadOnlyList<FlowStepRun> Steps { get; init; } = [];
    public IReadOnlyList<RuntimeExecutionBinding> RuntimeBindings { get; init; } = [];
    public DurableRuntimeStateReference? RuntimeState { get; init; }
    public string? ExecutionLeaseId { get; init; }
    public DateTimeOffset? ExecutionLeaseExpiresAt { get; init; }
}
