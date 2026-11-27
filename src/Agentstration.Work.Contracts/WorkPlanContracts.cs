namespace Agentstration.Work.Contracts;

public sealed record WorkPlanDefinitionRequest(string Intent, IReadOnlyList<Guid>? TaskIds,
    IReadOnlyList<WorkTaskDependencyRequest?>? Dependencies)
{
    public IReadOnlyList<WorkPlanTaskConnectionsRequest> TaskConnections { get; init; } = [];
}
public sealed record WorkTaskDependencyRequest(Guid TaskId, Guid DependsOnTaskId);
public sealed record WorkPlanTaskConnectionsRequest(Guid TaskId, IReadOnlyList<string> Names);
public sealed record CreateWorkPlanRequest(Guid Id, Guid CommandId, WorkPlanDefinitionRequest? Definition);
public sealed record ReviseWorkPlanRequest(Guid CommandId, long ExpectedVersion, WorkPlanDefinitionRequest? Definition);
public sealed record TransitionWorkPlanRequest(Guid CommandId, long ExpectedVersion, long Revision, string RevisionDigest);
public sealed record WorkPlanTaskReferenceResponse(Guid TaskId, string CreationDigest);
public sealed record WorkPlanRevisionResponse(Guid PlanId, long Number, string Intent, IReadOnlyList<Guid> TaskIds,
    IReadOnlyList<WorkTaskDependencyRequest> Dependencies, IReadOnlyList<WorkPlanTaskReferenceResponse> Tasks,
    string Digest, DateTimeOffset CreatedAt)
{
    public IReadOnlyList<WorkPlanTaskConnectionsRequest> TaskConnections { get; init; } = [];
}
public sealed record WorkPlanTaskStateResponse(Guid TaskId, long Version, WorkTaskOutcome Outcome, WorkPlanTaskReadiness Readiness,
    IReadOnlyList<string> Reasons, IReadOnlyList<Guid> UnmetDependencies);
public sealed record WorkPlanResponse(Guid Id, Guid WorkspaceId, WorkPlanStatus Status, long Version, long CurrentRevision,
    long? ValidatedRevision, string? ValidatedDigest, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    WorkPlanRevisionResponse Revision, IReadOnlyList<WorkPlanTaskStateResponse> Tasks)
{
    public WorkPlanExecutionResponse? Execution { get; init; }
    public IReadOnlyList<WorkPlanAttemptResponse> Attempts { get; init; } = [];
}

public sealed record WorkPlanExecutionTargetRequest(Guid TaskId, Agentstration.Flows.FlowReference Flow, System.Text.Json.JsonElement Input)
{
    public IReadOnlyList<string> Connections { get; init; } = [];
}
public sealed record StartWorkPlanExecutionRequest(Guid CommandId, long ExpectedVersion, long Revision, string RevisionDigest,
    IReadOnlyList<WorkPlanExecutionTargetRequest> Targets)
{
    public Guid? ProjectId { get; init; }
}
public sealed record ControlWorkPlanExecutionRequest(Guid CommandId, long ExpectedVersion, Guid? TaskId = null);
public sealed record WorkPlanExecutionResponse(long Revision, string RevisionDigest, bool Enabled)
{
    public Guid? ProjectId { get; init; }
}
public sealed record WorkPlanAttemptResponse(Guid Id, Guid TaskId, long PlanRevision, string PlanDigest, long TaskVersion, string TaskDigest,
    Guid WorkItemId, string FlowRunId, Agentstration.Flows.FlowReference Flow, IReadOnlyList<WorkPlanTaskObservationResponse> Dependencies,
    WorkPlanAttemptStatus Status, bool CancellationRequested, DateTimeOffset AdmittedAt, DateTimeOffset UpdatedAt, string? Diagnostic,
    IReadOnlyList<WorkPlanAdmissionEvidence> AdmissionEvidence)
{
    public WorkProjectContextSnapshotResponse? ProjectContext { get; init; }
}
public sealed record WorkPlanTaskObservationResponse(Guid TaskId, long Version);
public sealed record WorkPlanCommandResponse(Guid PlanId, Guid CommandId, long PlanVersion, WorkPlanAction Action, string IntentDigest, long Revision,
    string RevisionDigest, DateTimeOffset AppliedAt, IReadOnlyList<WorkPlanTaskObservationResponse> ObservedTasks);
