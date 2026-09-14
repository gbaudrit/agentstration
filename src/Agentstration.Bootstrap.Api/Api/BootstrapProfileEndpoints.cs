using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static class BootstrapProfileEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationBootstrapProfiles(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/bootstrap").RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/profiles", GetAsync);
        group.MapPost("/source-profile", GetSourceProfileAsync);
        group.MapPost("/binding-targets", GetBindingTargetsAsync);
        group.MapPost("/profiles/preview", PreviewAsync);
        group.MapPost("/applications", ApplyAsync);
        group.MapGet("/applications/{applicationId}", GetApplicationAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSourceProfileAsync(
        BootstrapSourceProfileSelection request,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () => Results.Ok(await service.GetSourceProfileAsync(request, requestContext.Current.PrincipalId, cancellationToken)));

    private static async Task<IResult> GetBindingTargetsAsync(
        BootstrapBindingTargetsRequest request,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () => Results.Ok(await service.GetBindingTargetsAsync(
            request.Target,
            request.TargetKind,
            request.Profiles ?? [],
            requestContext.Current.PrincipalId,
            cancellationToken,
            request.Source)));

    private static async Task<IResult> GetAsync(
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAsync(requestContext.Current.PrincipalId, cancellationToken));

    private static async Task<IResult> PreviewAsync(
        BootstrapProfilePreviewRequest request,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () => Results.Ok(await service.PreviewAsync(
            new(request.Profiles ?? [], request.Target, request.Bindings, request.Source),
            requestContext.Current.PrincipalId,
            cancellationToken)));

    private static async Task<IResult> GetApplicationAsync(
        string applicationId,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var result = await service.GetApplicationAsync(applicationId, requestContext.Current.PrincipalId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ApplyAsync(
        ApplyBootstrapProfilesRequest request,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var result = await service.ApplyAsync(
                new(request.Profiles ?? [], request.Target, request.Bindings, request.Source),
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
        catch (BootstrapRequestException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "bootstrap_invalid", detail: exception.Message);
        }
    }
}
