using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace Agentstration.Web;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationIdentityApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").RequireAuthorization(AgentstrationPolicies.Authenticated);
        group.MapGet("/context", async (IdentityExperienceService service, CancellationToken token) => Results.Ok(await service.GetContextAsync(token)))
            .RequireAuthorization(AgentstrationPolicies.WorkspaceReader);
        group.MapPost("/context/workspace", SelectWorkspaceAsync);
        group.MapGet("/organization", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok(await service.GetCurrentAsync(token))).RequireAuthorization(AgentstrationPolicies.AuthorizationReader);
        group.MapGet("/workspaces", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok((await service.GetCurrentAsync(token)).Workspaces)).RequireAuthorization(AgentstrationPolicies.AuthorizationReader);
        group.MapGet("/workspaces/{workspaceId:guid}", GetWorkspaceAsync);
        group.MapPost("/workspaces", CreateWorkspaceAsync).RequireAuthorization(AgentstrationPolicies.WorkspaceAdmin);
        group.MapGet("/workspaces/{workspaceId:guid}/memberships", ListWorkspaceMembershipsAsync)
            .RequireAuthorization(AgentstrationPolicies.AuthorizationReader);
        group.MapPut("/workspaces/{workspaceId:guid}/memberships/{principalId:guid}", SetWorkspaceMembershipAsync)
            .RequireAuthorization(AgentstrationPolicies.AuthorizationAdmin);
        group.MapDelete("/workspaces/{workspaceId:guid}/memberships/{principalId:guid}", RemoveWorkspaceMembershipAsync)
            .RequireAuthorization(AgentstrationPolicies.AuthorizationAdmin);
        group.MapGet("/members", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok((await service.GetCurrentAsync(token)).Members)).RequireAuthorization(AgentstrationPolicies.AuthorizationReader);
        group.MapGet("/platform", () => Results.Ok(new { role = "PlatformAdmin" }))
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        endpoints.MapPost("/bff/identity/context/workspace", SelectWorkspaceAsync)
            .RequireAuthorization(AgentstrationPolicies.Authenticated);
        return endpoints;
    }

    private static async Task<IResult> GetWorkspaceAsync(
        Guid workspaceId,
        IIdentityStore store,
        Microsoft.AspNetCore.Authorization.IAuthorizationService authorization,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var workspace = await store.GetWorkspaceAsync(workspaceId, cancellationToken);
        if (workspace is null) return Results.NotFound();
        var decision = await authorization.AuthorizeAsync(httpContext.User, workspace, AgentstrationPolicies.WorkspaceReader);
        return decision.Succeeded ? Results.Ok(workspace) : Results.Forbid();
    }

    private static async Task<IResult> SelectWorkspaceAsync(
        SelectWorkspaceRequest request,
        IdentityExperienceService service,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await service.ValidateWorkspaceSelectionAsync(request.WorkspaceId, cancellationToken);
            response.Cookies.Append(RequestContextMiddleware.WorkspaceCookie, context.WorkspaceId.ToString("D"), new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = httpRequest.IsHttps,
                MaxAge = TimeSpan.FromDays(30)
            });
            return Results.Ok(context);
        }
        catch (AuthorizationDeniedException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "workspace_access_denied", detail: exception.Message);
        }
    }

    private static async Task<IResult> CreateWorkspaceAsync(
        CreateWorkspaceRequest request,
        IdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var workspace = await service.CreateWorkspaceAsync(request.Name, request.DisplayName, cancellationToken);
            return Results.Created($"/api/identity/workspaces/{workspace.Id:D}", workspace);
        }
        catch (AuthorizationDeniedException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "permission_denied", detail: exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "workspace_invalid", detail: exception.Message);
        }
        catch (ControlPlaneConcurrencyException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "workspace_conflict", detail: exception.Message);
        }
    }

    private static async Task<IResult> ListWorkspaceMembershipsAsync(
        Guid workspaceId,
        WorkspaceMembershipAdministrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(workspaceId, cancellationToken));

    private static async Task<IResult> SetWorkspaceMembershipAsync(
        Guid workspaceId,
        Guid principalId,
        SetWorkspaceMembershipRequest request,
        WorkspaceMembershipAdministrationService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.SetAsync(workspaceId, principalId, request.Role, cancellationToken)); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["membership"] = [exception.Message] }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static async Task<IResult> RemoveWorkspaceMembershipAsync(
        Guid workspaceId,
        Guid principalId,
        WorkspaceMembershipAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.RemoveAsync(workspaceId, principalId, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }
}

public sealed record SelectWorkspaceRequest(Guid WorkspaceId);
public sealed record CreateWorkspaceRequest(string Name, string DisplayName);
public sealed record SetWorkspaceMembershipRequest(string Role);
