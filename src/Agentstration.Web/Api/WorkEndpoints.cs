using System.Globalization;
using Agentstration.Application.Work;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Web;

public static class WorkEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationWorkApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work/workitems").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
        group.MapPost("/", CreateAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/", ListAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        group.MapGet("/{workItemId:guid}", GetAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        group.MapPost("/{workItemId:guid}/cancel", CancelAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/{workItemId:guid}/messages", AddMessageAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/{workItemId:guid}/input", ProvideInputAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/{workItemId:guid}/approval", SubmitApprovalAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/{workItemId:guid}/events", GetEventsAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        group.MapGet("/{workItemId:guid}/result", GetResultAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
        return endpoints;
    }

    private static Task<IResult> CreateAsync(CreateWorkItemRequest request, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var inputs = request.Inputs?.Select(value => new WorkInput(value.Text, value.Structured, value.Metadata)).ToArray();
            var attachments = request.Attachments?.Select(ToWorkAttachment).ToArray();
            WorkCorrelationId? correlation = string.IsNullOrWhiteSpace(request.CorrelationId) ? null : new WorkCorrelationId(request.CorrelationId.Trim());
            var stored = await service.SubmitAsync(new SubmitWorkItemCommand(
                CurrentWorkspace(requestContext), request.Type, request.Instruction, request.Title, request.Description, request.RequesterIdentity,
                correlation, request.RequestedAgentId, request.Metadata, inputs, attachments, request.Flow), token);
            SetEntityHeaders(response, stored);
            response.Headers.Location = $"/api/work/workitems/{stored.Value.Id}";
            return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
        });

    private static Task<IResult> ListAsync(
        int? skip,
        int? top,
        WorkItemStatus? status,
        string? type,
        string? requester,
        string? agent,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        WorkItemSortField? sortBy,
        WorkItemSortDirection? sortDirection,
        WorkItemService service,
        ICurrentRequestContext requestContext,
        CancellationToken token) => ExecuteAsync(async () =>
        {
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 50, 1, 200);
            var query = new WorkItemQuery(
                CurrentWorkspace(requestContext), actualSkip, actualTop, status, type, requester, agent, createdFrom, createdTo,
                sortBy ?? WorkItemSortField.CreatedAt, sortDirection ?? WorkItemSortDirection.Descending);
            var page = await service.QueryAsync(query, token);
            var next = page.HasMore ? NextLink(query with { Skip = actualSkip + actualTop }) : null;
            return Results.Ok(new WorkItemPageResponse(page.Items.Select(value => ToSummary(value.Value)).ToArray(), next));
        });

    private static Task<IResult> GetAsync(Guid workItemId, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var stored = await RequiredAsync(CurrentWorkspace(requestContext), workItemId, service, token);
            SetEntityHeaders(response, stored);
            return Results.Ok(ToResponse(stored.Value));
        });

    private static Task<IResult> GetResultAsync(Guid workItemId, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var stored = await RequiredAsync(CurrentWorkspace(requestContext), workItemId, service, token);
            SetEntityHeaders(response, stored);
            return stored.Value.Result is null
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "result_not_available", detail: "The work item does not have a final result yet.")
                : Results.Ok(ToResult(stored.Value.Result));
        });

    private static Task<IResult> GetEventsAsync(Guid workItemId, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var stored = await RequiredAsync(CurrentWorkspace(requestContext), workItemId, service, token);
            return Results.Ok(stored.Value.History.Select(value => new WorkEventResponse(value.EventId, value.Sequence, value.Type, value.Origin, value.OccurredAt, value.Metadata)));
        });

    private static Task<IResult> CancelAsync(Guid workItemId, HttpRequest request, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var workspaceId = CurrentWorkspace(requestContext);
            await RequireIfMatchAsync(workspaceId, workItemId, request, service, token);
            var stored = await service.CancelAsync(workspaceId, new WorkItemId(workItemId), null, token);
            SetEntityHeaders(response, stored);
            return Results.Ok(ToResponse(stored.Value));
        });

    private static Task<IResult> AddMessageAsync(Guid workItemId, AddWorkMessageRequest body, HttpRequest request, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var workspaceId = CurrentWorkspace(requestContext);
            await RequireIfMatchAsync(workspaceId, workItemId, request, service, token);
            var stored = await service.AddMessageAsync(workspaceId, new WorkItemId(workItemId), body.Content, body.AuthorId, token);
            SetEntityHeaders(response, stored);
            return Results.Ok(ToResponse(stored.Value));
        });

    private static Task<IResult> ProvideInputAsync(Guid workItemId, ProvideWorkInputRequest body, HttpRequest request, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(body.Text) && body.Structured is null) throw new WorkValidationException("input_required", "Text or structured input is required.");
            var workspaceId = CurrentWorkspace(requestContext);
            await RequireIfMatchAsync(workspaceId, workItemId, request, service, token);
            var stored = await service.ProvideInputAsync(workspaceId, new WorkItemId(workItemId), new WorkInput(body.Text, body.Structured, body.Metadata), body.AuthorId, token);
            SetEntityHeaders(response, stored);
            return Results.Ok(ToResponse(stored.Value));
        });

    private static Task<IResult> SubmitApprovalAsync(Guid workItemId, SubmitWorkApprovalRequest body, HttpRequest request, HttpResponse response, WorkItemService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var workspaceId = CurrentWorkspace(requestContext);
            await RequireIfMatchAsync(workspaceId, workItemId, request, service, token);
            var stored = await service.SubmitApprovalAsync(workspaceId, new WorkItemId(workItemId), body.Decision, body.AuthorId, body.Comment, token);
            SetEntityHeaders(response, stored);
            return Results.Ok(ToResponse(stored.Value));
        });

    internal static WorkAttachment ToWorkAttachment(WorkAttachmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new WorkValidationException("attachment_name_required", "An attachment name is required.");
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out _)) throw new WorkValidationException("attachment_uri_invalid", "An attachment must reference an absolute URI.");
        return new WorkAttachment(request.Name.Trim(), new WorkContentReference(request.Uri, request.MediaType, request.Name), request.Size, request.Metadata);
    }

    private static async Task<StoredWorkItem> RequiredAsync(WorkspaceId workspaceId, Guid id, WorkItemService service, CancellationToken token) =>
        await service.GetAsync(workspaceId, new WorkItemId(id), token) ?? throw new KeyNotFoundException($"Work item '{id}' was not found.");

    private static async Task RequireIfMatchAsync(WorkspaceId workspaceId, Guid id, HttpRequest request, WorkItemService service, CancellationToken token)
    {
        var supplied = request.Headers.IfMatch.FirstOrDefault();
        if (supplied is null) return;
        var current = await RequiredAsync(workspaceId, id, service, token);
        if (!string.Equals(current.ETag, supplied, StringComparison.Ordinal)) throw new WorkItemConcurrencyException("The supplied ETag does not match the current work item version.");
    }

    private static void SetEntityHeaders(HttpResponse response, StoredWorkItem stored) => response.Headers.ETag = stored.ETag;
    private static WorkspaceId CurrentWorkspace(ICurrentRequestContext requestContext) => new(requestContext.Current.WorkspaceId);

    private static WorkItemResponse ToResponse(WorkItem item) => new(
        item.Id.Value, item.Type, item.Title, item.Instruction, item.Description, item.Status, item.CreatedAt, item.UpdatedAt,
        item.RequesterIdentity, item.CorrelationId.Value, item.Metadata, item.RequestedAgentId, item.Flow, item.SelectedAgentId,
        item.CurrentExecutionId?.Value, item.Inputs, item.Attachments, item.Messages, item.Interactions,
        item.Result is null ? null : ToResult(item.Result),
        item.Error is null ? null : new WorkErrorResponse(item.Error.Code, item.Error.Message, item.Error.Category, item.Error.IsRecoverable, item.Error.OccurredAt, item.Error.ExecutionId?.Value),
        item.Version);

    private static WorkItemSummaryResponse ToSummary(WorkItem item) => new(item.Id.Value, item.Type, item.Title, item.Status, item.CreatedAt, item.UpdatedAt, item.RequesterIdentity, item.SelectedAgentId, item.Version);
    private static WorkResultResponse ToResult(WorkResult result) => new(result.Contents, result.Artifacts, result.Metadata, result.CreatedAt);

    private static string NextLink(WorkItemQuery query)
    {
        var values = new List<string>
        {
            $"skip={query.Skip.ToString(CultureInfo.InvariantCulture)}",
            $"top={query.Take.ToString(CultureInfo.InvariantCulture)}",
            $"sortBy={query.SortBy}",
            $"sortDirection={query.SortDirection}"
        };
        Add("status", query.Status?.ToString());
        Add("type", query.Type);
        Add("requester", query.RequesterIdentity);
        Add("agent", query.AgentId);
        Add("createdFrom", query.CreatedFrom?.ToString("O", CultureInfo.InvariantCulture));
        Add("createdTo", query.CreatedTo?.ToString("O", CultureInfo.InvariantCulture));
        return $"/api/work/workitems?{string.Join('&', values)}";

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "workitem_not_found", detail: exception.Message); }
        catch (WorkItemConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (WorkValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (WorkTransitionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }
}
