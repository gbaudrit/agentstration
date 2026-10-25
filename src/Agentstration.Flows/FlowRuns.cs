using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flows;

public sealed record FlowRunError(string Code, string Message, string? Details = null);
public sealed record FlowRunScope(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Guid TenantId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] WorkspaceId WorkspaceId,
    Guid PrincipalId)
{
    public ResourceScopeRef? LogicalScopeRef { get; init; }

    public ResourceScopeRef ScopeRef => WorkspaceId.Value != Guid.Empty
        ? ResourceScopeRef.Workspace(WorkspaceId.Value)
        : TenantId != Guid.Empty
            ? ResourceScopeRef.Tenant(TenantId)
            : ResourceScopeRef.Instance;

    [JsonIgnore]
    public ResourceScopeRef AccessScopeRef => LogicalScopeRef ?? ScopeRef;

    [JsonIgnore]
    public ExactExecutionScope Exact => ExactExecutionScope.Create(
        ScopeRef,
        PrincipalId,
        TenantId == Guid.Empty ? null : TenantId,
        WorkspaceId.Value == Guid.Empty ? null : WorkspaceId.Value);

    public static FlowRunScope Instance(Guid principalId) =>
        new(Guid.Empty, default, principalId);

    public static FlowRunScope Tenant(Guid tenantId, Guid principalId) =>
        new(tenantId, default, principalId);

    public static FlowRunScope Workspace(Guid tenantId, WorkspaceId workspaceId, Guid principalId) =>
        new(tenantId, workspaceId, principalId);
}
public readonly record struct FlowRunKey(ResourceScopeRef ScopeRef, string RunId)
{
    public FlowRunKey(WorkspaceId workspaceId, string runId) : this(ResourceScopeRef.Workspace(workspaceId.Value), runId) { }

    [JsonIgnore]
    public WorkspaceId WorkspaceId => ScopeRef.Kind == ResourceScopeKind.Workspace
        ? new WorkspaceId(ScopeRef.TargetId!.Value)
        : default;
}
[method: JsonConstructor]
public sealed record FlowRunEvent(ResourceScopeRef ScopeRef, string RunId, long Sequence, FlowRunEventType Type, string? StepId, JsonElement? Payload, DateTimeOffset Timestamp)
{
    public FlowRunEvent(WorkspaceId workspaceId, string runId, long sequence, FlowRunEventType type, string? stepId,
        JsonElement? payload, DateTimeOffset timestamp)
        : this(ResourceScopeRef.Workspace(workspaceId.Value), runId, sequence, type, stepId, payload, timestamp) { }

    [JsonIgnore]
    public WorkspaceId WorkspaceId => ScopeRef.Kind == ResourceScopeKind.Workspace
        ? new WorkspaceId(ScopeRef.TargetId!.Value)
        : default;
}
public sealed record FlowStepRunUsage(int? InputTokens = null, int? OutputTokens = null, int? TotalTokens = null);
public enum FlowRunTokenUsageState { Collecting, Final }
public enum FlowRunTokenUsageCoverage { Complete, Partial, Unavailable }
public sealed record FlowRunTokenUsageTotal(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    FlowRunTokenUsageCoverage Coverage,
    int ReportedInvocationCount,
    int InvocationCount);
public sealed record FlowRunTokenUsage(
    FlowRunTokenUsageState State,
    FlowRunTokenUsageTotal Direct,
    FlowRunTokenUsageTotal Inclusive)
{
    private static readonly FlowRunTokenUsageTotal Empty = new(null, null, null,
        FlowRunTokenUsageCoverage.Unavailable, 0, 0);

    public static FlowRunTokenUsage Initial { get; } = new(FlowRunTokenUsageState.Collecting, Empty, Empty);
}
public sealed record FlowStepArtifactReference(
    string ArtifactId,
    string FileName,
    string MediaType,
    string Kind = "staged",
    string? StorageFlowRunId = null,
    string? LocalArtifactId = null);
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
public sealed record FlowToolRouteResolution(
    string ToolSetName,
    ResourceNamespace ToolSetNamespace,
    string ToolSetVersion,
    string Capability,
    string Route,
    string ToolName,
    ResourceNamespace ToolNamespace,
    Guid ToolUid,
    long ToolGeneration,
    string ProviderName,
    ResourceNamespace ProviderNamespace);
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
    public string? ChildFlowRunId { get; init; }
    public IReadOnlyList<string> ChildFlowRunIds { get; init; } = [];
    public int? RepeatIteration { get; init; }
    public FlowToolRouteResolution? ToolRoute { get; init; }
    public IReadOnlyList<FlowStepArtifactReference> Artifacts { get; init; } = [];
    public string? ArtifactStorageFlowRunId { get; init; }
}

public sealed record FlowRun
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WorkspaceId WorkspaceId { get; init; }
    public required string Id { get; init; }
    public required FlowId FlowId { get; init; }
    public required string FlowVersion { get; init; }
    public FlowDefinitionState DefinitionState { get; init; } = FlowDefinitionState.Published;
    public long? DraftRevision { get; init; }
    public string? DefinitionSnapshotId { get; init; }
    public string? DefinitionHash { get; init; }
    public ResourceScopeRef? DefinitionScopeRef { get; init; }
    public string? DeploymentResourceId { get; init; }
    public FlowRunStatus Status { get; init; } = FlowRunStatus.Pending;
    public FlowRunTrigger Trigger { get; init; }
    public string? StartedBy { get; init; }
    public FlowInvocationOrigin? InvocationOrigin { get; init; }
    public string? CallerId { get; init; }
    public string? CausationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? CorrelationId { get; init; }
    public string? WorkItemResourceId { get; init; }
    public bool ResolvedFromActiveReference { get; init; }
    public string? ParentFlowRunId { get; init; }
    public string? RootFlowRunId { get; init; }
    public int NestingDepth { get; init; }
    public string? InteractionId { get; init; }
    public string? WorkTaskId { get; init; }
    public string? TriggerMessageId { get; init; }
    public required FlowRunScope Scope { get; init; }
    public ResourceScopeRef ExecutionScopeRef => Scope.ScopeRef;
    public required JsonElement Input { get; init; }
    public JsonElement? Output { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowOutputOutcome? OutputOutcome { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public FlowRunError? Error { get; init; }
    public required FlowVersion DefinitionSnapshot { get; init; }
    public IReadOnlyList<FlowStepRun> Steps { get; init; } = [];
    public FlowRunTokenUsage TokenUsage { get; init; } = FlowRunTokenUsage.Initial;
    public IReadOnlyList<RuntimeExecutionBinding> RuntimeBindings { get; init; } = [];
    public DurableRuntimeStateReference? RuntimeState { get; init; }
    public string? ExecutionLeaseId { get; init; }
    public DateTimeOffset? ExecutionLeaseExpiresAt { get; init; }
}
