namespace Agentstration.Work.Contracts;

public sealed record CreateWorkTaskTreeRequest(Guid RootTaskId, IReadOnlyList<CreateWorkTaskRequest?>? Tasks);
public sealed record CreateWorkTaskRequest(Guid Id, WorkTaskKind Kind, string Title, string Instruction,
    Guid? ParentTaskId = null, string? Description = null);
public sealed record WorkTaskTreeResponse(Guid WorkspaceId, Guid RootTaskId, IReadOnlyList<WorkTaskTreeNodeResponse> Tasks);
public sealed record WorkTaskTreeNodeResponse(Guid Id, WorkTaskKind Kind, Guid? ParentTaskId, string Title,
    string Instruction, string? Description, WorkItemStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
