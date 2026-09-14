using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentstration.ResourcePlanning.Api;

public static class ResourcePlanningApiModule
{
    public static IEndpointRouteBuilder MapResourcePlanningApi(this IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapGroup("/api/resource-plans").RequireAuthorization(AgentstrationPolicies.Authenticated);
        plans.MapPost("/", CreateAsync).Produces<ResourcePlan>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        plans.MapGet("/", ListAsync).Produces<ResourcePlanPage>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        plans.MapGet("/{id:guid}", GetAsync).Produces<ResourcePlan>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        plans.MapPut("/{id:guid}", RefineAsync).Produces<ResourcePlan>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        plans.MapPost("/{id:guid}/status", ChangeStatusAsync).Produces<ResourcePlan>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        plans.MapGet("/{id:guid}/activities", ListActivitiesAsync).Produces<ResourcePlanActivity[]>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        plans.MapPost("/validate-content", ValidateContent).Produces<PlanningValidationResult>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        plans.MapPost("/{id:guid}/materializations", MaterializeAsync).Produces<ResourcePlanMaterialization>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        return endpoints;
    }

    private static Task<IResult> CreateAsync(
        CreateResourcePlanRequest request,
        HttpResponse response,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var current = RequireWorkspace(context);
        var stored = await service.CreateAsync(Scope(current), request, current.PrincipalId, cancellationToken);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/resource-plans/{stored.Value.Id}";
        return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> ListAsync(
        ResourcePlanStatus? status,
        int? skip,
        int? take,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok(await service.ListAsync(Scope(RequireWorkspace(context)), status, skip ?? 0, take ?? 50, cancellationToken)));

    private static Task<IResult> GetAsync(
        Guid id,
        HttpResponse response,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var stored = await service.GetAsync(Scope(RequireWorkspace(context)), new(id), cancellationToken)
            ?? throw new ResourcePlanNotFoundException(new(id));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> RefineAsync(
        Guid id,
        RefineResourcePlanRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var current = RequireWorkspace(context);
        var stored = await service.RefineAsync(Scope(current), new(id), request, httpRequest.Headers.IfMatch.ToString(), current.PrincipalId, cancellationToken);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> ChangeStatusAsync(
        Guid id,
        ChangeResourcePlanStatusRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var current = RequireWorkspace(context);
        var stored = await service.ChangeStatusAsync(Scope(current), new(id), request, httpRequest.Headers.IfMatch.ToString(), current.PrincipalId, cancellationToken);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> ListActivitiesAsync(
        Guid id,
        ResourcePlanService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok(await service.ListActivitiesAsync(Scope(RequireWorkspace(context)), new(id), cancellationToken)));

    private static RequestContext RequireWorkspace(ICurrentRequestContext context) =>
        context.IsInitialized ? context.Current : throw new UnauthorizedAccessException("A Workspace request context is required.");

    private static ResourcePlanScope Scope(RequestContext context) => new(context.TenantId, new WorkspaceId(context.WorkspaceId));

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourcePlanNotFoundException exception) { return Results.NotFound(Problem("resource_plan_not_found", exception.Message, StatusCodes.Status404NotFound)); }
        catch (ResourcePlanConcurrencyException exception) { return Results.Conflict(Problem("resource_plan_concurrency", exception.Message, StatusCodes.Status409Conflict)); }
        catch (ResourcePlanLifecycleException exception) { return Results.UnprocessableEntity(Problem(exception.Code, exception.Message, StatusCodes.Status422UnprocessableEntity)); }
        catch (ResourcePlanValidationException exception) { return Results.UnprocessableEntity(new { title = "resource_plan_content_invalid", detail = exception.Message, status = StatusCodes.Status422UnprocessableEntity, errors = exception.Issues }); }
        catch (ArgumentException exception) { return Results.BadRequest(Problem("resource_plan_invalid", exception.Message, StatusCodes.Status400BadRequest)); }
    }

    private static object Problem(string title, string detail, int status) => new { title, detail, status };

    private static IResult ValidateContent(ResourcePlanContent content, IResourcePlanContentValidator validator) => Results.Ok(validator.Validate(content));

    private static Task<IResult> MaterializeAsync(
        Guid id,
        ResourcePlanMaterializationService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok(await service.MaterializeAsync(Scope(RequireWorkspace(context)), new(id), cancellationToken)));
}
