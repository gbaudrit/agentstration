using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Web;

public static class WorkOperationsEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationWorkOperationsApi(this IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup("/api/tasks").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
        tasks.MapGet("/", ListAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/summary", SummaryAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}", DetailAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}/activities", ActivitiesAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}/flow-runs", FlowRunsAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}/flow-runs/{runId}", FlowRunAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}/pending-actions", PendingActionsAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapPost("/{taskId:guid}/pending-actions/{actionId:guid}/respond", RespondPendingActionAsync)
            .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanRunFlows);
        tasks.MapGet("/{taskId:guid}/results", ResultsAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapGet("/{taskId:guid}/artifacts", ArtifactsAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        tasks.MapPost("/{taskId:guid}/pause", PauseAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        tasks.MapPost("/{taskId:guid}/resume", ResumeAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        tasks.MapPost("/{taskId:guid}/cancel", CancelAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        return endpoints;
    }

    private static Task<IResult> ListAsync(
        WorkTaskStatus? status, string? search, bool? hasPendingAction,
        int? page, int? pageSize, string? sort, string? direction,
        WorkplaceService service, FlowRunService flowRuns, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualPage = Math.Max(1, page ?? 1); var actualPageSize = Math.Clamp(pageSize ?? 25, 1, 100);
        var sortField = string.Equals(sort, "createdAt", StringComparison.OrdinalIgnoreCase) ? WorkItemSortField.CreatedAt : WorkItemSortField.UpdatedAt;
        var sortDirection = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? WorkItemSortDirection.Ascending : WorkItemSortDirection.Descending;
        var scope = CurrentScope(requestContext);
        var result = await service.QueryOperationalTasksAsync(scope.WorkspaceId, status, search, hasPendingAction, actualPage, actualPageSize, sortField, sortDirection, token);
        var summaries = new List<WorkTaskOperationsSummary>(result.Items.Count);
        foreach (var task in result.Items) summaries.Add(await SummaryOfAsync(task, service, flowRuns, scope, token));
        return Results.Ok(new WorkTaskOperationsPageResponse(summaries, actualPage, actualPageSize, result.TotalCount));
    });

    private static Task<IResult> SummaryAsync(WorkplaceService service, FlowRunService flowRuns, ICurrentRequestContext requestContext, TimeProvider timeProvider, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        async Task<int> Count(WorkTaskStatus status, DateTimeOffset? updatedFrom = null) =>
            (await service.QueryOperationalTasksAsync(scope.WorkspaceId, status, null, null, 1, 1, WorkItemSortField.UpdatedAt, WorkItemSortDirection.Descending, token, updatedFrom)).TotalCount;
        return Results.Ok(new WorkTaskOperationsCountersResponse(
            await Count(WorkTaskStatus.Running), await Count(WorkTaskStatus.ActionRequired), await Count(WorkTaskStatus.Paused),
            await Count(WorkTaskStatus.Failed), await Count(WorkTaskStatus.Completed, timeProvider.GetUtcNow().AddHours(-24))));
    });

    private static Task<IResult> DetailAsync(Guid taskId, WorkplaceService service, FlowRunService flowRuns, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var operational = await service.GetOperationalTaskAsync(scope.WorkspaceId, new(taskId), token); var task = operational.Task;
        var interaction = task.InteractionId is { } interactionId ? await service.GetInteractionAsync(operational.WorkspaceId, interactionId, token) : null;
        var pending = (await service.ListPendingActionsForTaskAsync(operational.WorkspaceId, task.Id, token)).Select(WorkplaceService.ToContract).ToArray();
        var results = await service.ListResultsAsync(operational.WorkspaceId, task.Id, token);
        var artifacts = await service.ListArtifactsAsync(operational.WorkspaceId, task.Id, token);
        var activities = await service.ListActivitiesAsync(operational.WorkspaceId, task.Id, token);
        var messages = task.InteractionId is { } messageInteractionId ? await service.ListMessagesAsync(operational.WorkspaceId, messageInteractionId, token) : [];
        var runs = await RunsForAsync(task.Id, flowRuns, scope, results, artifacts, token);
        return Results.Ok(new WorkTaskOperationsDetailResponse(
            await SummaryOfAsync(task, service, flowRuns, scope, token), interaction is null ? null : ToInteraction(interaction), pending, runs,
            results.Select(ToResult).ToArray(), artifacts.Select(value => ToArtifact(value, operational.WorkspaceId)).ToArray(), activities, messages));
    });

    private static Task<IResult> ActivitiesAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.ListActivitiesAsync(value.WorkspaceId, value.Task.Id, token)), token);
    private static Task<IResult> PendingActionsAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok((await service.ListPendingActionsForTaskAsync(value.WorkspaceId, value.Task.Id, token)).Select(WorkplaceService.ToContract)), token);
    private static Task<IResult> RespondPendingActionAsync(Guid taskId, Guid actionId, TaskPendingActionResponse body, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value =>
    {
        var resolved = await service.RespondTaskPendingActionAsync(value.WorkspaceId, value.Task.Id, new(actionId), body.Values, requestContext.Current.PrincipalId.ToString("D"), token);
        return Results.Ok(WorkplaceService.ToContract(resolved.PendingAction));
    }, token);
    private static Task<IResult> ResultsAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok((await service.ListResultsAsync(value.WorkspaceId, value.Task.Id, token)).Select(ToResult)), token);
    private static Task<IResult> ArtifactsAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok((await service.ListArtifactsAsync(value.WorkspaceId, value.Task.Id, token)).Select(artifact => ToArtifact(artifact, value.WorkspaceId))), token);
    private static Task<IResult> FlowRunsAsync(Guid taskId, WorkplaceService service, FlowRunService flowRuns, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value =>
    {
        var results = await service.ListResultsAsync(value.WorkspaceId, value.Task.Id, token); var artifacts = await service.ListArtifactsAsync(value.WorkspaceId, value.Task.Id, token);
        return Results.Ok(await RunsForAsync(value.Task.Id, flowRuns, CurrentScope(requestContext), results, artifacts, token));
    }, token);
    private static Task<IResult> FlowRunAsync(Guid taskId, string runId, WorkplaceService service, FlowRunService flowRuns, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value =>
    {
        var run = await flowRuns.GetAsync(value.WorkspaceId, runId, token);
        if (run is null || !string.Equals(run.Value.WorkTaskId, value.Task.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"FlowRun '{runId}' was not found for Task '{taskId}'.");
        return Results.Ok(run.Value);
    }, token);
    private static Task<IResult> PauseAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.PauseTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);
    private static Task<IResult> ResumeAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.ResumeTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);
    private static Task<IResult> CancelAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.CancelTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);

    private static Task<IResult> WithTaskAsync(Guid id, WorkplaceService service, ICurrentRequestContext requestContext, Func<(WorkspaceId WorkspaceId, WorkTask Task), Task<IResult>> action, CancellationToken token) =>
        ExecuteAsync(async () => await action(await service.GetOperationalTaskAsync(CurrentScope(requestContext).WorkspaceId, new(id), token)));

    private static async Task<WorkTaskOperationsSummary> SummaryOfAsync(WorkTask task, WorkplaceService service, FlowRunService flowRuns, FlowRunScope scope, CancellationToken token)
    {
        var activities = await service.ListActivitiesAsync(task.WorkspaceId, task.Id, token);
        var results = await service.ListResultsAsync(task.WorkspaceId, task.Id, token);
        var artifacts = await service.ListArtifactsAsync(task.WorkspaceId, task.Id, token);
        var pending = (await service.ListPendingActionsForTaskAsync(task.WorkspaceId, task.Id, token)).Count(value => value.Status == PendingActionStatus.Pending);
        var runs = await RunsForAsync(task.Id, flowRuns, scope, results, artifacts, token);
        var started = activities.FirstOrDefault(value => value.Type == WorkTaskActivityType.TaskStarted)?.CreatedAt;
        var completed = activities.LastOrDefault(value => value.Type is WorkTaskActivityType.TaskCompleted or WorkTaskActivityType.TaskFailed or WorkTaskActivityType.TaskCancelled)?.CreatedAt;
        var error = task.Error is null ? null : new WorkTaskErrorResponse(task.Error.Code, "Task failed", task.Error.Message, task.Error.OccurredAt, task.FlowRunId, task.Error.IsRecoverable);
        return new(task.Id.Value, task.WorkspaceId.ToString(), task.EntryId?.Value, task.InteractionId?.Value, task.Title, task.Description, task.Status,
            task.CreatedAt, started, task.UpdatedAt, completed, task.FlowRunId, results.LastOrDefault()?.Id.Value, pending, results.Count, artifacts.Count, runs.Count,
            activities.LastOrDefault()?.Title, error);
    }

    private static async Task<IReadOnlyList<WorkTaskFlowRunResponse>> RunsForAsync(WorkTaskId taskId, FlowRunService service, FlowRunScope scope, IReadOnlyList<WorkTaskResult> results, IReadOnlyList<WorkTaskArtifact> artifacts, CancellationToken token)
    {
        var page = await service.ListAsync(null, null, 0, 200, scope, token);
        return page.Items.Select(value => value.Value).Where(value => value.WorkTaskId == taskId.ToString()).OrderBy(value => value.CreatedAt)
            .Select(value => new WorkTaskFlowRunResponse(value.Id, value.FlowId.Value, value.Status.ToString(), value.CreatedAt, value.StartedAt, value.CompletedAt,
                value.ParentFlowRunId is null ? "Initial request" : "Conversation continuation", value.ParentFlowRunId,
                Guid.TryParse(value.TriggerMessageId, out var triggerId) ? triggerId : null,
                results.Count(result => result.FlowRunId == value.Id), artifacts.Count(artifact => artifact.FlowRunId == value.Id))).ToArray();
    }

    private static WorkTaskResultResponse ToResult(WorkTaskResult value) => new(value.Id.Value, value.FlowRunId, value.Kind, value.Title, value.Content, value.CreatedAt, value.Sequence);
    private static WorkTaskArtifactResponse ToArtifact(WorkTaskArtifact value, WorkspaceId workspaceId) => new(value.Id.Value, value.FlowRunId, value.Name, value.ContentType, value.Length, value.CreatedAt, value.Sequence,
        $"/api/workspaces/{Uri.EscapeDataString(WorkspaceName(workspaceId))}/tasks/{value.WorkTaskId}/artifacts/{value.Id}/content");
    private static InteractionResponse ToInteraction(WorkplaceInteraction value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId.Value, value.Status, value.StartedAt, value.LastActivityAt, value.InputValues, value.Attachments, value.Messages, value.PendingActionId?.Value, value.TaskId?.Value, value.ImmediateResult, value.Version, value.LastFlowRunId, value.LastTriggerMessageId) { EntryNamespace = value.EntryId.Namespace };
    private static string WorkspaceName(WorkspaceId id) => id.ToString();
    private static FlowRunScope CurrentScope(ICurrentRequestContext requestContext)
    {
        var current = requestContext.Current;
        return new FlowRunScope(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "work_task_not_found", detail: exception.Message); }
        catch (WorkValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (WorkTransitionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
    }
}

public sealed record TaskPendingActionResponse(IReadOnlyDictionary<string, System.Text.Json.JsonElement> Values);
