using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Web.Hosting;

namespace Agentstration.Web;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationIdentityApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity");
        group.MapGet("/context", async (IdentityExperienceService service, CancellationToken token) => Results.Ok(await service.GetContextAsync(token)));
        group.MapPost("/context/workspace", SelectWorkspaceAsync);
        group.MapGet("/organization", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok(await service.GetCurrentAsync(token)));
        group.MapGet("/workspaces", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok((await service.GetCurrentAsync(token)).Workspaces));
        group.MapPost("/workspaces", CreateWorkspaceAsync);
        group.MapGet("/members", async (IdentityAdministrationService service, CancellationToken token) =>
            Results.Ok((await service.GetCurrentAsync(token)).Members));
        return endpoints;
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
}

public sealed record SelectWorkspaceRequest(Guid WorkspaceId);
public sealed record CreateWorkspaceRequest(string Name, string DisplayName);
