using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static class BootstrapProfileEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationBootstrapProfiles(this IEndpointRouteBuilder endpoints)
    {
        var startup = endpoints.MapGroup("/api/bootstrap/startup").RequireAuthorization(AgentstrationPolicies.Authenticated);
        startup.MapGet("/admission", (IBootstrapStartupStatus status, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new BootstrapStartupAdmission(status.Read().Ready));
        }).RequireAuthorization(AgentstrationPolicies.InteractiveUser)
            .Produces<BootstrapStartupAdmission>().WithSummary("Check authenticated Console startup admission");
        startup.MapGet("", (IBootstrapStartupStatus status, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(status.Read());
        }).RequireAuthorization(AgentstrationPolicies.PlatformAdmin).Produces<BootstrapStartupSnapshot>().WithSummary("Read current Bootstrap startup progress");
        startup.MapGet("/resources", (Guid attemptId, string? stepId, string? kind, string? state, string? search, string? groupId, string? followResourceId,
            int? offset, int? limit, IBootstrapStartupStatus status, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (offset is < 0 || limit is < 1 or > 100 || search?.Length > 128 || followResourceId?.Length > 128)
                return Results.Problem(statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "invalid_startup_query" });
            var page = status.ReadResources(new(attemptId, stepId, kind, state, search, offset ?? 0, limit ?? 25, groupId, followResourceId));
            return page is null ? Results.Problem(statusCode: 409,
                extensions: new Dictionary<string, object?> { ["code"] = "startup_attempt_changed" }) : Results.Ok(page);
        }).RequireAuthorization(AgentstrationPolicies.PlatformAdmin)
            .Produces<BootstrapStartupResourcePage>().ProducesProblem(400).ProducesProblem(409)
            .WithSummary("Read a bounded page of resources in the selected startup plan");
        startup.MapGet("/resources/{resourceId}/model-options", async (Guid attemptId, string resourceId,
            string? providerName, string? providerNamespace, IBootstrapStartupModelCorrections service, HttpContext context, CancellationToken token) =>
            await ExecuteModelChangeAsync(async () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.GetOptionsAsync(attemptId, resourceId,
                    context.Features.Get<ResolvedPrincipalFeature>()?.PrincipalId ?? Guid.Empty, providerName, providerNamespace, token));
            })).RequireAuthorization(AgentstrationPolicies.PlatformAdmin)
            .Produces<BootstrapStartupModelOptions>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Read cached model choices for a pending Bootstrap profile");
        startup.MapPost("/resources/{resourceId}/model-substitution", async (string resourceId, BootstrapStartupModelChange request,
            IBootstrapStartupModelCorrections service, HttpContext context, CancellationToken token) =>
            await ExecuteModelChangeAsync(async () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                await service.ApplyAsync(resourceId, request, context.Features.Get<ResolvedPrincipalFeature>()?.PrincipalId ?? Guid.Empty, token);
                return Results.Accepted($"/api/bootstrap/startup/resources?attemptId={request.AttemptId:D}",
                    new BootstrapStartupModelChangeAccepted(request.AttemptId, resourceId));
            })).RequireAuthorization(AgentstrationPolicies.PlatformAdmin)
            .Produces<BootstrapStartupModelChangeAccepted>(StatusCodes.Status202Accepted).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Accept a versioned provider and model substitution for a pending Bootstrap profile");
        var group = endpoints.MapGroup("/api/bootstrap").RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/profiles", GetAsync);
        group.MapGet("/applied-profiles", (IBootstrapVersionHistory history, CancellationToken token) => history.ListProfilesAsync(token))
            .Produces<IReadOnlyList<AppliedBootstrapProfile>>()
            .WithSummary("List durable Bootstrap profiles");
        group.MapGet("/applied-profiles/{profileId}/versions", (string profileId, IBootstrapVersionHistory history, CancellationToken token) =>
            history.ListVersionsAsync(profileId, token))
            .Produces<IReadOnlyList<AppliedBootstrapVersion>>()
            .WithSummary("List the immutable versions of a Bootstrap profile");
        group.MapGet("/applied-profiles/{profileId}/versions/{version}/applications", ListScopedHistoryAsync)
            .Produces<IReadOnlyList<ScopedBootstrapApplication>>().ProducesProblem(400)
            .WithSummary("List the applications of an exact Bootstrap version at one scope");
        group.MapGet("/scoped-applications/{id:guid}", GetScopedApplicationAsync)
            .Produces<ScopedBootstrapApplication>().Produces(404).ProducesProblem(400)
            .WithSummary("Get a Bootstrap application by its exact scope and identifier");
        group.MapPost("/source-profile", GetSourceProfileAsync);
        group.MapPost("/binding-targets", GetBindingTargetsAsync);
        group.MapPost("/profiles/preview", PreviewAsync);
        group.MapPost("/applications", ApplyAsync);
        group.MapGet("/applications/{applicationId}", GetApplicationAsync);
        group.MapPost("/applications/{applicationId}/cancel", CancelAsync);
        return endpoints;
    }

    private static async Task<IResult> ListScopedHistoryAsync(string profileId, string version, string scope,
        IBootstrapVersionHistory history, CancellationToken token)
    {
        if (!ResourceScopeRef.TryParse(scope, out var exactScope))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "bootstrap_scope_invalid");
        return Results.Ok(await history.ListHistoryAsync(exactScope, profileId, version, token));
    }

    private static async Task<IResult> GetScopedApplicationAsync(Guid id, string scope,
        IBootstrapVersionHistory history, CancellationToken token)
    {
        if (!ResourceScopeRef.TryParse(scope, out var exactScope))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "bootstrap_scope_invalid");
        var application = await history.GetAsync(exactScope, id, token);
        return application is null ? Results.NotFound() : Results.Ok(application);
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
        await ExecuteAsync(async () => Results.Ok(request.Applications is { Count: > 0 }
            ? await service.GetPlanBindingTargetsAsync(request, requestContext.Current.PrincipalId, cancellationToken)
            : await service.GetBindingTargetsAsync(
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
            new(request.Profiles ?? [], request.Target, request.Bindings, request.Source, request.Applications),
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

    private static async Task<IResult> CancelAsync(
        string applicationId,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var application = await service.CancelAsync(applicationId, requestContext.Current.PrincipalId, cancellationToken);
            return application is null ? Results.NotFound() : Results.Accepted($"/api/bootstrap/applications/{applicationId}", application);
        });

    private static async Task<IResult> ApplyAsync(
        ApplyBootstrapProfilesRequest request,
        IBootstrapProfileApiService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var result = await service.StartAsync(request with { Profiles = request.Profiles ?? [] }, requestContext.Current.PrincipalId, cancellationToken);
            return Results.Accepted($"/api/bootstrap/applications/{result.Metadata.Name}", result);
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

    private static async Task<IResult> ExecuteModelChangeAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (BootstrapStartupModelChangeException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Code,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
    }
}
