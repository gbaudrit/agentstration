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
        group.MapGet("/preferences", GetPreferencesAsync);
        group.MapPut("/preferences", UpdatePreferencesAsync);
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
        group.MapGet("/platform-administrators", async (PlatformAdministratorAdministrationService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(token)))
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapPut("/platform-administrators/{principalId:guid}", GrantPlatformAdministratorAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapDelete("/platform-administrators/{principalId:guid}", RevokePlatformAdministratorAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/principals/{principalId:guid}/external-identities", ListExternalIdentitiesAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapPost("/principals/{principalId:guid}/external-identities", LinkExternalIdentityAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapDelete("/principals/{principalId:guid}/external-identities/{externalIdentityId:guid}", UnlinkExternalIdentityAsync)
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/audit-events", async (int? limit, SecurityAuditService service, CancellationToken token) =>
            Results.Ok(await service.ListLatestAsync(limit ?? 100, token)))
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        endpoints.MapPost("/bff/identity/context/workspace", SelectWorkspaceAsync)
            .RequireAuthorization(AgentstrationPolicies.Authenticated);
        return endpoints;
    }

    private static async Task<IResult> GetPreferencesAsync(
        HttpContext context,
        PrincipalPreferencesService service,
        CancellationToken cancellationToken)
    {
        var principal = context.Features.Get<ResolvedPrincipalFeature>()?.Principal;
        if (principal is null) return Results.Forbid();
        return Results.Ok(ToResponse(await service.GetAsync(principal.Id, cancellationToken)));
    }

    private static async Task<IResult> UpdatePreferencesAsync(
        UpdatePrincipalPreferencesRequest request,
        HttpContext context,
        PrincipalPreferencesService service,
        CancellationToken cancellationToken)
    {
        var principal = context.Features.Get<ResolvedPrincipalFeature>()?.Principal;
        if (principal is null) return Results.Forbid();
        try
        {
            return Results.Ok(ToResponse(await service.UpdateAsync(principal.Id, request.Theme, cancellationToken)));
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["theme"] = [exception.Message] });
        }
    }

    private static PrincipalPreferencesResponse ToResponse(PrincipalPreferences preferences) =>
        new(preferences.Theme.ToString(), preferences.UpdatedAt);

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

    private static async Task<IResult> GrantPlatformAdministratorAsync(
        Guid principalId,
        PlatformAdministratorAdministrationService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.GrantAsync(principalId, cancellationToken)); }
        catch (ArgumentException exception) { return Results.NotFound(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static async Task<IResult> RevokePlatformAdministratorAsync(
        Guid principalId,
        PlatformAdministratorAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.RevokeAsync(principalId, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static async Task<IResult> ListExternalIdentitiesAsync(
        Guid principalId,
        ExternalIdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.ListAsync(principalId, cancellationToken)); }
        catch (ArgumentException exception) { return Results.NotFound(new { error = exception.Message }); }
    }

    private static async Task<IResult> LinkExternalIdentityAsync(
        Guid principalId,
        LinkExternalIdentityRequest request,
        ExternalIdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await service.LinkAsync(principalId, request.Issuer, request.Subject, cancellationToken);
            return Results.Ok(identity);
        }
        catch (ArgumentException exception) when (exception.ParamName == "principalId")
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [exception.ParamName ?? "identity"] = [exception.Message] });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ControlPlaneConcurrencyException)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> UnlinkExternalIdentityAsync(
        Guid principalId,
        Guid externalIdentityId,
        ExternalIdentityAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.UnlinkAsync(principalId, externalIdentityId, cancellationToken);
            return Results.NoContent();
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }
}

public sealed record SelectWorkspaceRequest(Guid WorkspaceId);
public sealed record CreateWorkspaceRequest(string Name, string DisplayName);
public sealed record SetWorkspaceMembershipRequest(string Role);
public sealed record UpdatePrincipalPreferencesRequest(string Theme);
public sealed record PrincipalPreferencesResponse(string Theme, DateTimeOffset UpdatedAt);
