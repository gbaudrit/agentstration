using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure;

public sealed class WorkOperationsQueryService(
    WorkplaceService workplace,
    FlowRunService flowRuns,
    TimeProvider timeProvider) : IWorkOperationsQueryService
{
    public async Task<WorkTaskOperationsPageResponse> ListAsync(
        WorkOperationsQueryScope scope, WorkTaskStatus? status, string? search, bool? hasPendingAction,
        int page, int pageSize, string? sort, string? direction, CancellationToken cancellationToken)
    {
        var actualPage = Math.Max(1, page);
        var actualPageSize = Math.Clamp(pageSize, 1, 100);
        var sortField = string.Equals(sort, "createdAt", StringComparison.OrdinalIgnoreCase) ? WorkItemSortField.CreatedAt : WorkItemSortField.UpdatedAt;
        var sortDirection = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? WorkItemSortDirection.Ascending : WorkItemSortDirection.Descending;
        var workspaceId = new WorkspaceId(scope.WorkspaceId);
        var result = await workplace.QueryOperationalTasksAsync(workspaceId, status, search, hasPendingAction, actualPage, actualPageSize, sortField, sortDirection, cancellationToken);
        var summaries = new List<WorkTaskOperationsSummary>(result.Items.Count);
        foreach (var task in result.Items)
            summaries.Add(await SummaryOfAsync(task, FlowScope(scope), cancellationToken));
        return new(summaries, actualPage, actualPageSize, result.TotalCount);
    }

    public async Task<WorkTaskOperationsCountersResponse> GetCountersAsync(WorkOperationsQueryScope scope, CancellationToken cancellationToken)
    {
        var workspaceId = new WorkspaceId(scope.WorkspaceId);
        async Task<int> Count(WorkTaskStatus status, DateTimeOffset? updatedFrom = null) =>
            (await workplace.QueryOperationalTasksAsync(workspaceId, status, null, null, 1, 1, WorkItemSortField.UpdatedAt, WorkItemSortDirection.Descending, cancellationToken, updatedFrom)).TotalCount;
        return new(await Count(WorkTaskStatus.Running), await Count(WorkTaskStatus.ActionRequired), await Count(WorkTaskStatus.Paused),
            await Count(WorkTaskStatus.Failed), await Count(WorkTaskStatus.Completed, timeProvider.GetUtcNow().AddHours(-24)));
    }

    public async Task<WorkTaskOperationsDetailProjection> GetDetailAsync(WorkOperationsQueryScope scope, Guid taskId, CancellationToken cancellationToken)
    {
        var operational = await workplace.GetOperationalTaskAsync(new WorkspaceId(scope.WorkspaceId), new(taskId), cancellationToken);
        var task = operational.Task;
        var interaction = task.InteractionId is { } interactionId ? await workplace.GetInteractionAsync(operational.WorkspaceId, interactionId, cancellationToken) : null;
        var pending = (await workplace.ListPendingActionsForTaskAsync(operational.WorkspaceId, task.Id, cancellationToken)).Select(WorkplaceService.ToContract).ToArray();
        var results = await workplace.ListResultsAsync(operational.WorkspaceId, task.Id, cancellationToken);
        var artifacts = await workplace.ListArtifactsAsync(operational.WorkspaceId, task.Id, cancellationToken);
        var activities = await workplace.ListActivitiesAsync(operational.WorkspaceId, task.Id, cancellationToken);
        var messages = task.InteractionId is { } messageInteractionId ? await workplace.ListMessagesAsync(operational.WorkspaceId, messageInteractionId, cancellationToken) : [];
        var runs = await RunsForAsync(task.Id, FlowScope(scope), results, artifacts, cancellationToken);
        var value = new WorkTaskOperationsDetailResponse(
            await SummaryOfAsync(task, FlowScope(scope), cancellationToken), interaction is null ? null : ToInteraction(interaction), pending, runs,
            results.Select(ToResult).ToArray(), artifacts.Select(value => ToArtifact(value, operational.WorkspaceId)).ToArray(), activities, messages);
        return new(value, $"\"{task.Version}\"");
    }

    public async Task<IReadOnlyList<WorkTaskFlowRunResponse>> ListFlowRunsAsync(WorkOperationsQueryScope scope, Guid taskId, CancellationToken cancellationToken)
    {
        var operational = await workplace.GetOperationalTaskAsync(new WorkspaceId(scope.WorkspaceId), new(taskId), cancellationToken);
        var results = await workplace.ListResultsAsync(operational.WorkspaceId, operational.Task.Id, cancellationToken);
        var artifacts = await workplace.ListArtifactsAsync(operational.WorkspaceId, operational.Task.Id, cancellationToken);
        return await RunsForAsync(operational.Task.Id, FlowScope(scope), results, artifacts, cancellationToken);
    }

    public async Task<object> GetFlowRunAsync(WorkOperationsQueryScope scope, Guid taskId, string runId, CancellationToken cancellationToken)
    {
        var operational = await workplace.GetOperationalTaskAsync(new WorkspaceId(scope.WorkspaceId), new(taskId), cancellationToken);
        var run = await flowRuns.GetAsync(runId, FlowScope(scope), cancellationToken);
        if (run is null || !string.Equals(run.Value.WorkTaskId, operational.Task.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"FlowRun '{runId}' was not found for Task '{taskId}'.");
        return run.Value;
    }

    private async Task<WorkTaskOperationsSummary> SummaryOfAsync(WorkTask task, FlowRunScope scope, CancellationToken token)
    {
        var activities = await workplace.ListActivitiesAsync(task.WorkspaceId, task.Id, token);
        var results = await workplace.ListResultsAsync(task.WorkspaceId, task.Id, token);
        var artifacts = await workplace.ListArtifactsAsync(task.WorkspaceId, task.Id, token);
        var pending = (await workplace.ListPendingActionsForTaskAsync(task.WorkspaceId, task.Id, token)).Count(value => value.Status == PendingActionStatus.Pending);
        var runs = await RunsForAsync(task.Id, scope, results, artifacts, token);
        var started = activities.FirstOrDefault(value => value.Type == WorkTaskActivityType.TaskStarted)?.CreatedAt;
        var completed = activities.LastOrDefault(value => value.Type is WorkTaskActivityType.TaskCompleted or WorkTaskActivityType.TaskFailed or WorkTaskActivityType.TaskCancelled)?.CreatedAt;
        var error = task.Error is null ? null : new WorkTaskErrorResponse(task.Error.Code, "Task failed", task.Error.Message, task.Error.OccurredAt, task.FlowRunId, task.Error.IsRecoverable);
        return new(task.Id.Value, task.WorkspaceId.ToString(), task.EntryId?.Value, task.InteractionId?.Value, task.Title, task.Description, task.Status,
            task.CreatedAt, started, task.UpdatedAt, completed, task.FlowRunId, results.LastOrDefault()?.Id.Value, pending, results.Count, artifacts.Count, runs.Count,
            activities.LastOrDefault()?.Title, error);
    }

    private async Task<IReadOnlyList<WorkTaskFlowRunResponse>> RunsForAsync(WorkTaskId taskId, FlowRunScope scope, IReadOnlyList<WorkTaskResult> results, IReadOnlyList<WorkTaskArtifact> artifacts, CancellationToken token)
    {
        var page = await flowRuns.ListAsync(null, null, 0, 200, scope, token);
        return page.Items.Select(value => value.Value).Where(value => value.WorkTaskId == taskId.ToString()).OrderBy(value => value.CreatedAt)
            .Select(value => new WorkTaskFlowRunResponse(value.Id, value.FlowId.Value, value.Status.ToString(), value.CreatedAt, value.StartedAt, value.CompletedAt,
                value.ParentFlowRunId is null ? "Initial request" : "Conversation continuation", value.ParentFlowRunId,
                Guid.TryParse(value.TriggerMessageId, out var triggerId) ? triggerId : null,
                results.Count(result => result.FlowRunId == value.Id), artifacts.Count(artifact => artifact.FlowRunId == value.Id))).ToArray();
    }

    private static WorkTaskResultResponse ToResult(WorkTaskResult value) => new(value.Id.Value, value.FlowRunId, value.Kind, value.Title, value.Content, value.CreatedAt, value.Sequence);
    private static WorkTaskArtifactResponse ToArtifact(WorkTaskArtifact value, WorkspaceId workspaceId) => new(value.Id.Value, value.FlowRunId, value.Name, value.ContentType, value.Length, value.CreatedAt, value.Sequence,
        $"/api/workspaces/{Uri.EscapeDataString(workspaceId.ToString())}/tasks/{value.WorkTaskId}/artifacts/{value.Id}/content");
    private static InteractionResponse ToInteraction(WorkplaceInteraction value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId.Value, value.Status, value.StartedAt, value.LastActivityAt, value.InputValues, value.Attachments, value.Messages, value.PendingActionId?.Value, value.TaskId?.Value, value.ImmediateResult, value.Version, value.LastFlowRunId, value.LastTriggerMessageId) { EntryNamespace = value.EntryId.Namespace };
    private static FlowRunScope FlowScope(WorkOperationsQueryScope scope) => new(scope.TenantId, new WorkspaceId(scope.WorkspaceId), scope.PrincipalId);
}
