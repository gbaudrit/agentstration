using Agentstration.Identity.Contracts;
using Agentstration.Application.Work;
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
        tasks.MapDelete("/{taskId:guid}", DeleteAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanDeleteRuns);
        return endpoints;
    }

    private static Task<IResult> ListAsync(
        WorkTaskStatus? status, string? search, bool? hasPendingAction,
        int? page, int? pageSize, string? sort, string? direction,
        IWorkOperationsQueryService queries, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        return Results.Ok(await queries.ListAsync(CurrentScope(requestContext), status, search, hasPendingAction, page ?? 1, pageSize ?? 25, sort, direction, token));
    });

    private static Task<IResult> SummaryAsync(IWorkOperationsQueryService queries, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await queries.GetCountersAsync(CurrentScope(requestContext), token)));

    private static Task<IResult> DetailAsync(Guid taskId, HttpResponse response, IWorkOperationsQueryService queries, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var projection = await queries.GetDetailAsync(CurrentScope(requestContext), taskId, token);
        response.Headers.ETag = projection.ETag;
        return Results.Ok(projection.Value);
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
    private static Task<IResult> FlowRunsAsync(Guid taskId, IWorkOperationsQueryService queries, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await queries.ListFlowRunsAsync(CurrentScope(requestContext), taskId, token)));
    private static Task<IResult> FlowRunAsync(Guid taskId, string runId, IWorkOperationsQueryService queries, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await queries.GetFlowRunAsync(CurrentScope(requestContext), taskId, runId, token)));
    private static Task<IResult> PauseAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.PauseTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);
    private static Task<IResult> ResumeAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.ResumeTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);
    private static Task<IResult> CancelAsync(Guid taskId, WorkplaceService service, ICurrentRequestContext requestContext, CancellationToken token) => WithTaskAsync(taskId, service, requestContext, async value => Results.Ok(await service.CancelTaskAsync(value.WorkspaceId, value.Task.Id, token)), token);

    private static Task<IResult> DeleteAsync(
        Guid taskId,
        HttpRequest request,
        WorkTaskDeletionService service,
        ICurrentRequestContext requestContext,
        CancellationToken token) => ExecuteAsync(async () =>
    {
        var expectedETag = request.Headers.IfMatch.FirstOrDefault()
            ?? throw new WorkValidationException("if_match_required", "Deleting a Task requires an If-Match ETag.");
        await service.DeleteAsync(new WorkspaceId(CurrentScope(requestContext).WorkspaceId), new(taskId), expectedETag, token);
        return Results.NoContent();
    });

    private static Task<IResult> WithTaskAsync(Guid id, WorkplaceService service, ICurrentRequestContext requestContext, Func<(WorkspaceId WorkspaceId, WorkTask Task), Task<IResult>> action, CancellationToken token) =>
        ExecuteAsync(async () => await action(await service.GetOperationalTaskAsync(new WorkspaceId(CurrentScope(requestContext).WorkspaceId), new(id), token)));

    private static WorkTaskResultResponse ToResult(WorkTaskResult value) => new(value.Id.Value, value.FlowRunId, value.Kind, value.Title, value.Content, value.CreatedAt, value.Sequence);
    private static WorkTaskArtifactResponse ToArtifact(WorkTaskArtifact value, WorkspaceId workspaceId) => new(value.Id.Value, value.FlowRunId, value.Name, value.ContentType, value.Length, value.CreatedAt, value.Sequence,
        $"/api/workspaces/{Uri.EscapeDataString(WorkspaceName(workspaceId))}/tasks/{value.WorkTaskId}/artifacts/{value.Id}/content");
    private static string WorkspaceName(WorkspaceId id) => id.ToString();
    private static WorkOperationsQueryScope CurrentScope(ICurrentRequestContext requestContext)
    {
        var current = requestContext.Current;
        return new(current.TenantId, current.WorkspaceId, current.PrincipalId);
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
