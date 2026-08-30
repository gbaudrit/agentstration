using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkplaceService
{
    public async Task<WorkTask> GetTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), cancellationToken))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        var continuations = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 1, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), cancellationToken);
        return ProjectTask(anchor, LatestExecution(anchor, continuations.Items.Select(value => value.Value).ToArray()), taskId);
    }

    public async Task<OperationalWorkTaskPage> QueryOperationalTasksAsync(
        WorkspaceId workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction,
        int page, int pageSize, WorkItemSortField sort, WorkItemSortDirection direction, CancellationToken cancellationToken,
        DateTimeOffset? updatedFrom = null, DateTimeOffset? updatedTo = null)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = new WorkItemQuery(
            workspaceId, Skip: (page - 1) * pageSize, Take: pageSize, Status: status is null ? null : ToItemStatus(status.Value),
            SortBy: sort, SortDirection: direction,
            IsContinuation: false, Search: search, HasPendingAction: hasPendingAction, OperationalTasks: true,
            UpdatedFrom: updatedFrom, UpdatedTo: updatedTo);
        var anchors = await workItems.QueryAsync(query, cancellationToken);
        var tasks = new List<WorkTask>(anchors.Items.Count);
        foreach (var stored in anchors.Items)
        {
            var anchor = stored.Value; var taskId = WorkTaskId.FromWorkItem(anchor.Id);
            RequireWorkspace(anchor, workspaceId);
            var continuations = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 1, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), cancellationToken);
            tasks.Add(ProjectTask(anchor, LatestExecution(anchor, continuations.Items.Select(value => value.Value).ToArray()), taskId));
        }
        return new OperationalWorkTaskPage(tasks, Math.Max(0, anchors.TotalCount));
    }

    public async Task<IReadOnlyList<WorkTask>> ListTasksAsync(WorkspaceId workspaceId, WorkTaskStatus? status, CancellationToken cancellationToken)
    {
        var anchors = await ListRootWorkItemsAsync(workspaceId, cancellationToken);
        var tasks = new List<WorkTask>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var taskId = WorkTaskId.FromWorkItem(anchor.Id);
            var continuations = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 1, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), cancellationToken);
            var task = ProjectTask(anchor, LatestExecution(anchor, continuations.Items.Select(value => value.Value).ToArray()), taskId);
            if (status is null || task.Status == status) tasks.Add(task);
        }
        return tasks.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.Id.Value).ToArray();
    }

    public async Task<WorkTask> PauseTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.PauseAsync(workspaceId, current.Id, token); return await GetTaskAsync(workspaceId, taskId, token); }

    public async Task<WorkTask> ResumeTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.ResumeAsync(workspaceId, current.Id, token); return await GetTaskAsync(workspaceId, taskId, token); }

    public async Task<WorkTask> CancelTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.CancelAsync(workspaceId, current.Id, null, token); return await GetTaskAsync(workspaceId, taskId, token); }

    public Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListActivitiesAsync(workspaceId, taskId, token);

    public Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListResultsAsync(workspaceId, taskId, token);

    public Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListArtifactsAsync(workspaceId, taskId, token);

    public async Task<WorkTaskArtifact> GetArtifactAsync(WorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken token) => await repository.GetArtifactAsync(workspaceId, taskId, artifactId, token) ?? throw new KeyNotFoundException($"Artifact '{artifactId}' was not found in Workspace '{workspaceId}'.");

    public static WorkplaceAction CurrentAction(WorkTask task) => task.Status switch { WorkTaskStatus.ActionRequired => new RespondAction("A response is required."), WorkTaskStatus.Failed => new ShowErrorAction(task.Error?.Code ?? "Task failed", task.Error?.Message), WorkTaskStatus.Completed => new ShowResultAction("Result", task.Result?.Contents.FirstOrDefault()?.Text, task.Result?.Contents.FirstOrDefault()?.Structured), _ => new RespondAction("Agentstration is working on your request.") };

    private async Task<WorkItem> GetCurrentExecutionAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), token))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        var page = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 1, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), token);
        return LatestExecution(anchor, page.Items.Select(value => value.Value).ToArray());
    }

    private async Task<IReadOnlyList<WorkItem>> ListRootWorkItemsAsync(WorkspaceId workspaceId, CancellationToken token)
    {
        var items = new List<WorkItem>();
        var skip = 0;
        while (true)
        {
            var page = await workItems.QueryAsync(new WorkItemQuery(
                workspaceId,
                Skip: skip,
                Take: WorkItemQueryPageSize,
                IsContinuation: false,
                SortBy: WorkItemSortField.CreatedAt), token);
            items.AddRange(page.Items.Select(value => value.Value));
            if (!page.HasMore || page.Items.Count == 0) break;
            skip += page.Items.Count;
        }
        return items;
    }

    private static WorkItem LatestExecution(WorkItem anchor, IReadOnlyList<WorkItem> items) => items
        .Append(anchor)
        .Where(value => value.Id == anchor.Id || value.Metadata.GetValueOrDefault(TaskMetadata) == WorkTaskId.FromWorkItem(anchor.Id).ToString())
        .OrderByDescending(value => value.CreatedAt)
        .First();

    private static WorkItemStatus ToItemStatus(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Draft => WorkItemStatus.Pending,
        WorkTaskStatus.Pending => WorkItemStatus.Pending,
        WorkTaskStatus.Running => WorkItemStatus.Running,
        WorkTaskStatus.ActionRequired => WorkItemStatus.WaitingForInput,
        WorkTaskStatus.Paused => WorkItemStatus.Paused,
        WorkTaskStatus.Completed => WorkItemStatus.Completed,
        WorkTaskStatus.Failed => WorkItemStatus.Failed,
        WorkTaskStatus.Cancelled => WorkItemStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static WorkTask ProjectTask(WorkItem anchor, WorkItem latest, WorkTaskId publicId)
    {
        var projected = ToTask(latest, publicId);
        return projected with { Title = anchor.Title ?? anchor.Instruction, Description = anchor.Description, CreatedAt = anchor.CreatedAt };
    }

    private static void RequireWorkspace(WorkItem item, WorkspaceId workspaceId) { if (item.WorkspaceId != workspaceId) throw new KeyNotFoundException($"Task '{item.Id}' was not found in Workspace '{workspaceId}'."); }

    internal static WorkTask ToTask(WorkItem item, WorkTaskId? publicId = null) { var isTrigger = item.Metadata.GetValueOrDefault("origin") == "trigger"; item.Metadata.TryGetValue(EntryMetadata, out var entryId); item.Metadata.TryGetValue(InteractionMetadata, out var interactionId); if (!isTrigger && (entryId is null || !Guid.TryParse(interactionId, out _))) throw new InvalidOperationException($"Work item '{item.Id}' is not a Workplace Task."); item.Metadata.TryGetValue(FlowRunMetadata, out var flowRunId); if (flowRunId is null) item.Result?.Metadata.TryGetValue(FlowRunMetadata, out flowRunId); return new WorkTask(publicId ?? WorkTaskId.FromWorkItem(item.Id), item.WorkspaceId, entryId is null ? null : new(entryId), Guid.TryParse(interactionId, out var interactionGuid) ? new(interactionGuid) : null, item.Title ?? item.Instruction, item.Description, ToTaskStatus(item.Status), item.CreatedAt, item.UpdatedAt, flowRunId, item.Messages, item.Interactions, item.Result?.Artifacts ?? [], item.Result, item.Error, item.Version); }

    internal static WorkTaskStatus ToTaskStatus(WorkItemStatus status) => status switch { WorkItemStatus.Pending or WorkItemStatus.Queued => WorkTaskStatus.Pending, WorkItemStatus.Running => WorkTaskStatus.Running, WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval => WorkTaskStatus.ActionRequired, WorkItemStatus.Paused => WorkTaskStatus.Paused, WorkItemStatus.Completed => WorkTaskStatus.Completed, WorkItemStatus.Failed => WorkTaskStatus.Failed, WorkItemStatus.Cancelled => WorkTaskStatus.Cancelled, _ => throw new ArgumentOutOfRangeException(nameof(status), status, null) };
}

