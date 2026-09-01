using Agentstration.Management.Abstractions;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public sealed record BootstrapProfilePreviewRequest(
    IReadOnlyList<string> Profiles,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null);

public sealed record ApplyBootstrapProfilesRequest(
    IReadOnlyList<string> Profiles,
    string ExpectedDigest,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null);

public static class BootstrapProfileEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationBootstrapProfiles(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/bootstrap").RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/profiles", GetAsync);
        group.MapPost("/profiles/preview", PreviewAsync);
        group.MapPost("/applications", ApplyAsync);
        group.MapGet("/applications/{applicationId}", GetApplicationAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        BootstrapProfileManagementService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAsync(requestContext.Current.PrincipalId, cancellationToken));

    private static async Task<IResult> PreviewAsync(
        BootstrapProfilePreviewRequest request,
        BootstrapProfileManagementService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () => Results.Ok(await service.PreviewAsync(
            new(request.Profiles ?? [], request.Target, request.Bindings),
            requestContext.Current.PrincipalId,
            cancellationToken)));

    private static async Task<IResult> GetApplicationAsync(
        string applicationId,
        BootstrapProfileManagementService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var result = await service.GetApplicationAsync(applicationId, requestContext.Current.PrincipalId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ApplyAsync(
        ApplyBootstrapProfilesRequest request,
        BootstrapProfileManagementService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var result = await service.ApplyAsync(
                new(request.Profiles ?? [], request.Target, request.Bindings),
                request.ExpectedDigest,
                requestContext.Current.PrincipalId,
                cancellationToken);
            return Results.Created($"/api/bootstrap/applications/{result.Metadata.Name}", result);
        });

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (AuthorizationDeniedException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "permission_denied", detail: exception.Message);
        }
        catch (DeclarativeBootstrapException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "bootstrap_invalid", detail: exception.Message);
        }
    }
}
