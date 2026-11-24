namespace Agentstration.Work.Contracts;

public sealed record WorkPlanDefinitionRequest(string Intent, IReadOnlyList<Guid>? TaskIds,
    IReadOnlyList<WorkTaskDependencyRequest?>? Dependencies);
public sealed record WorkTaskDependencyRequest(Guid TaskId, Guid DependsOnTaskId);
public sealed record CreateWorkPlanRequest(Guid Id, Guid CommandId, WorkPlanDefinitionRequest? Definition);
public sealed record ReviseWorkPlanRequest(Guid CommandId, long ExpectedVersion, WorkPlanDefinitionRequest? Definition);
public sealed record TransitionWorkPlanRequest(Guid CommandId, long ExpectedVersion, long Revision, string RevisionDigest);
public sealed record WorkPlanTaskReferenceResponse(Guid TaskId, string CreationDigest);
public sealed record WorkPlanRevisionResponse(Guid PlanId, long Number, string Intent, IReadOnlyList<Guid> TaskIds,
    IReadOnlyList<WorkTaskDependencyRequest> Dependencies, IReadOnlyList<WorkPlanTaskReferenceResponse> Tasks,
    string Digest, DateTimeOffset CreatedAt);
public sealed record WorkPlanTaskStateResponse(Guid TaskId, long Version, WorkTaskOutcome Outcome, WorkPlanTaskReadiness Readiness,
    IReadOnlyList<string> Reasons, IReadOnlyList<Guid> UnmetDependencies);
public sealed record WorkPlanResponse(Guid Id, Guid WorkspaceId, WorkPlanStatus Status, long Version, long CurrentRevision,
    long? ValidatedRevision, string? ValidatedDigest, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    WorkPlanRevisionResponse Revision, IReadOnlyList<WorkPlanTaskStateResponse> Tasks);
public sealed record WorkPlanTaskObservationResponse(Guid TaskId, long Version);
public sealed record WorkPlanCommandResponse(Guid PlanId, Guid CommandId, long PlanVersion, WorkPlanAction Action, string IntentDigest, long Revision,
    string RevisionDigest, DateTimeOffset AppliedAt, IReadOnlyList<WorkPlanTaskObservationResponse> ObservedTasks);
