using Agentstration.Resources;

namespace Agentstration.Work.Contracts;

public sealed record WorkProjectDefinitionRequest(string DisplayName, string? Description, IReadOnlyList<Guid>? PlanIds, IReadOnlyList<Guid>? TaskIds);
public sealed record CreateWorkProjectRequest(Guid Id, Guid CommandId, string Name, WorkProjectDefinitionRequest? Definition)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record UpdateWorkProjectRequest(Guid CommandId, long ExpectedVersion, WorkProjectDefinitionRequest? Definition);
public sealed record WorkProjectResponse(Guid Id, Guid WorkspaceId, string Name, ResourceNamespace Namespace,
    string DisplayName, string? Description, IReadOnlyList<Guid> PlanIds, IReadOnlyList<Guid> TaskIds, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WorkProjectPageResponse(IReadOnlyList<WorkProjectResponse> Items, bool HasMore, int Skip, int Take);
