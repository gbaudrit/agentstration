using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkItemSortField { CreatedAt, UpdatedAt }
public enum WorkItemSortDirection { Ascending, Descending }

public sealed record WorkItemQuery(
    WorkspaceId WorkspaceId,
    Guid OwnerPrincipalId,
    int Skip = 0,
    int Take = 50,
    WorkItemStatus? Status = null,
    string? Type = null,
    string? RequesterIdentity = null,
    string? AgentId = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    WorkItemSortField SortBy = WorkItemSortField.CreatedAt,
    WorkItemSortDirection SortDirection = WorkItemSortDirection.Descending,
    string? InteractionId = null,
    string? EntryId = null,
    string? AnchorTaskId = null,
    bool? IsContinuation = null,
    string? Search = null,
    bool? HasPendingAction = null,
    bool OperationalTasks = false,
    bool WorkplaceTasksOnly = false,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null)
{
    public ResourceScopeRef ScopeRef { get; init; } = WorkspaceId.Value == Guid.Empty
        ? ResourceScopeRef.Instance
        : ResourceScopeRef.Workspace(WorkspaceId.Value);
}

public sealed record StoredWorkItem(WorkItem Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record WorkItemPage(IReadOnlyList<StoredWorkItem> Items, bool HasMore, int TotalCount = -1);
public sealed record DeletedWorkTask(IReadOnlyList<ArtifactReference> Artifacts);

public interface IWorkItemRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredWorkItem> CreateAsync(WorkItem workItem, CancellationToken cancellationToken);
    Task<StoredWorkItem?> GetAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkItemId id, CancellationToken cancellationToken);
    Task<StoredWorkItem?> GetExactAsync(ResourceScopeRef scopeRef, Guid ownerPrincipalId, WorkItemId id, CancellationToken cancellationToken) =>
        GetAsync(RequiredWorkspace(scopeRef), ownerPrincipalId, id, cancellationToken);
    Task<StoredWorkItem?> GetForExecutionAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken);
    Task<StoredWorkItem?> GetForExecutionExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken) =>
        GetForExecutionAsync(RequiredWorkspace(scopeRef), id, cancellationToken);
    Task<StoredWorkItem> SaveAsync(WorkItem workItem, long expectedVersion, CancellationToken cancellationToken);
    Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<WorkTaskId, StoredWorkItem>> ListLatestContinuationsAsync(
        WorkspaceId workspaceId,
        Guid ownerPrincipalId,
        IReadOnlyCollection<WorkTaskId> taskIds,
        CancellationToken cancellationToken);
    Task<DeletedWorkTask> DeleteTaskAsync(
        WorkspaceId workspaceId,
        Guid ownerPrincipalId,
        WorkTaskId taskId,
        string expectedETag,
        CancellationToken cancellationToken);

    private static WorkspaceId RequiredWorkspace(ResourceScopeRef scopeRef) =>
        scopeRef.Kind == ResourceScopeKind.Workspace && scopeRef.TargetId is { } workspaceId
            ? new WorkspaceId(workspaceId)
            : throw new WorkValidationException("work_execution_scope_unsupported", $"Execution scope '{scopeRef}' is not supported by this store.");
}

public sealed class WorkItemConcurrencyException(string message) : Exception(message);
