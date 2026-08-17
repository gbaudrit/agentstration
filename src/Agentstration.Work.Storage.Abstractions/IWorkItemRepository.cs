using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public enum WorkItemSortField { CreatedAt, UpdatedAt }
public enum WorkItemSortDirection { Ascending, Descending }

public sealed record WorkItemQuery(
    WorkspaceId WorkspaceId,
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
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null);

public sealed record StoredWorkItem(WorkItem Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record WorkItemPage(IReadOnlyList<StoredWorkItem> Items, bool HasMore, int TotalCount = -1);

public interface IWorkItemRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredWorkItem> CreateAsync(WorkItem workItem, CancellationToken cancellationToken);
    Task<StoredWorkItem?> GetAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken);
    Task<StoredWorkItem> SaveAsync(WorkItem workItem, long expectedVersion, CancellationToken cancellationToken);
    Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken);
}

public sealed class WorkItemConcurrencyException(string message) : Exception(message);
