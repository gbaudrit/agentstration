namespace Agentstration.Work.Contracts;

public sealed record CreateWorkTaskTreeRequest(Guid RootTaskId, IReadOnlyList<CreateWorkTaskRequest?>? Tasks);
public sealed record CreateWorkTaskRequest(Guid Id, WorkTaskKind Kind, string Title, string Instruction,
    Guid? ParentTaskId = null, string? Description = null, bool IsOptional = false);
public sealed record WorkTaskTreeResponse(Guid WorkspaceId, Guid RootTaskId, IReadOnlyList<WorkTaskTreeNodeResponse> Tasks);
public sealed record WorkTaskTreeNodeResponse(Guid Id, WorkTaskKind Kind, Guid? ParentTaskId, string Title,
    string Instruction, string? Description, WorkItemStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool IsOptional = false, WorkTaskAcceptanceResponse? Acceptance = null);
public sealed record RecordWorkTaskOutcomeRequest(Guid EventId, long ExpectedVersion, WorkTaskOutcome Outcome, string Evidence);
public sealed record WorkTaskEvidenceResponse(Guid EventId, WorkTaskOutcome Outcome, string Evidence, DateTimeOffset RecordedAt);
public sealed record WorkTaskChildOutcomeResponse(Guid TaskId, WorkTaskOutcome Outcome, long Version);
public sealed record WorkTaskAcceptanceResponse(WorkTaskOutcome Outcome, WorkTaskEvidenceResponse? Evidence,
    IReadOnlyList<WorkTaskChildOutcomeResponse> RequiredChildren, DateTimeOffset UpdatedAt);
