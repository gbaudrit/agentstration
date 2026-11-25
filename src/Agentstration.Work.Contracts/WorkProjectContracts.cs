using Agentstration.Resources;
using System.Text.Json.Serialization;

namespace Agentstration.Work.Contracts;

public sealed record WorkProjectDefinitionRequest(string DisplayName, string? Description, IReadOnlyList<Guid>? PlanIds, IReadOnlyList<Guid>? TaskIds)
{
    public IReadOnlyList<WorkProjectContextBinding>? Bindings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectSharingConnection? Sharing { get; init; }
}
public sealed record CreateWorkProjectRequest(Guid Id, Guid CommandId, string Name, WorkProjectDefinitionRequest? Definition)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record UpdateWorkProjectRequest(Guid CommandId, long ExpectedVersion, WorkProjectDefinitionRequest? Definition);
public sealed record WorkProjectResponse(Guid Id, Guid WorkspaceId, string Name, ResourceNamespace Namespace,
    string DisplayName, string? Description, IReadOnlyList<Guid> PlanIds, IReadOnlyList<Guid> TaskIds, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<WorkProjectContextBinding> Bindings { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectSharingConnection? Sharing { get; init; }
}
public sealed record WorkProjectContextResponse(Guid ProjectId, long ProjectVersion, IReadOnlyList<WorkProjectBindingAvailability> Bindings);
public sealed record WorkProjectContextSnapshotResponse(Guid ProjectId, long ProjectVersion, string Digest,
    IReadOnlyList<WorkProjectResolvedBinding> Bindings, DateTimeOffset CapturedAt);
public sealed record WorkProjectPageResponse(IReadOnlyList<WorkProjectResponse> Items, bool HasMore, int Skip, int Take);
