using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.ConnectedServices;

internal static class ConnectedServiceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var types = endpoints.MapGroup("/api/connected-service-types");
        types.MapGet("/", async (ConnectedServiceTypeManagementService service, CancellationToken token) =>
            Results.Ok((await service.ListAsync(token)).Select(value => value.Value)))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        types.MapGet("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpResponse response, ConnectedServiceTypeManagementService service, CancellationToken token) =>
            await Execute(async () =>
            {
                var stored = await service.GetExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name, token)
                    ?? throw new ConnectedServiceTypeNotFoundException(
                        ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name);
                response.Headers.ETag = stored.ETag;
                return Results.Ok(stored.Value);
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        types.MapPost("/", async (CreateConnectedServiceTypeRequest body, HttpResponse response,
            ConnectedServiceTypeManagementService service, CancellationToken token) => await Execute(async () => Resource(
                await service.CreateAsync(new ConnectedServiceTypeResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
                    Metadata = new()
                    {
                        Name = body.Name,
                        Namespace = body.Namespace ?? ResourceNamespace.Default
                    },
                    ScopeRef = body.ScopeRef,
                    Definition = body.Properties
                }, token), response, StatusCodes.Status201Created)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapPut("/{name}", async (string name, string scopeRef, string? @namespace,
            PutConnectedServiceTypeRequest body, HttpRequest request, HttpResponse response,
            ConnectedServiceTypeManagementService service, CancellationToken token) => await Execute(async () => Resource(
                await service.PutExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name, body.Properties,
                    request.Headers.IfMatch.FirstOrDefault(), token), response, StatusCodes.Status200OK)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapDelete("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpRequest request, ConnectedServiceTypeManagementService service, CancellationToken token) =>
            await Execute(async () =>
            {
                await service.DeleteExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        var services = endpoints.MapGroup("/api/connected-services");
        services.MapGet("/", async (ConnectedServiceManagementService service, CancellationToken token) =>
            Results.Ok((await service.ListAsync(token)).Select(value => value.Value)))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        services.MapGet("/{name}", async (string name, string? scopeRef, HttpResponse response,
            ConnectedServiceManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var scope = Scope(scopeRef);
                var stored = scope is null
                    ? await service.GetAsync(name, token)
                    : await service.GetExactAsync(scope.Value, name, token);
                if (stored is null) throw new ConnectedServiceNotFoundException(name);
                response.Headers.ETag = stored.ETag;
                return Results.Ok(stored.Value);
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        services.MapGet("/{name}/usages", async (string name, string scopeRef,
            ConnectedServiceManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var usages = (await service.GetUsagesAsync(ResourceScopeRef.Parse(scopeRef), name, token))
                    .Select(value => new ConnectedServiceUsageResponse(
                        value.Kind, value.Name, value.DisplayName, value.Url)).ToArray();
                return Results.Ok(new ConnectedServiceUsagesResponse(usages, usages.Length));
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        services.MapPost("/", async (CreateConnectedServiceRequest body, HttpResponse response,
            ConnectedServiceManagementService service, CancellationToken token) => await Execute(async () => Resource(
                await service.CreateAsync(new ConnectedServiceResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ConnectedServiceResourceKinds.ConnectedService,
                    Metadata = new() { Name = body.Name },
                    ScopeRef = body.ScopeRef,
                    Definition = body.Properties
                }, token), response, StatusCodes.Status201Created)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPut("/{name}", async (string name, string? scopeRef, PutConnectedServiceRequest body,
            HttpRequest request, HttpResponse response, ConnectedServiceManagementService service,
            CancellationToken token) => await Execute(async () => Resource(Scope(scopeRef) is { } scope
                ? await service.PutExactAsync(scope, name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token)
                : await service.PutAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPost("/{name}/reconcile", async (string name, string scopeRef, HttpRequest request,
            HttpResponse response, ConnectedServiceManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.ReconcileAsync(
                ResourceScopeRef.Parse(scopeRef), name, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPost("/{name}/deactivate", async (string name, string scopeRef, HttpRequest request,
            HttpResponse response, ConnectedServiceManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.DeactivateAsync(
                ResourceScopeRef.Parse(scopeRef), name, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapDelete("/{name}", async (string name, string scopeRef, HttpRequest request,
            ConnectedServiceManagementService service, CancellationToken token) => await Execute(async () =>
            {
                await service.DeleteAsync(ResourceScopeRef.Parse(scopeRef), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static ResourceScopeRef? Scope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);

    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ConnectedServiceNotFoundException exception)
        { return Results.Problem(statusCode: 404, title: "Connected Service not found", detail: exception.Message); }
        catch (ConnectedServiceAccessDeniedException exception)
        { return Results.Problem(statusCode: 403, title: "Connected Service access denied", detail: exception.Message); }
        catch (ConnectedServiceUnavailableException exception)
        { return Results.Problem(statusCode: 409, title: "Connected Service unavailable", detail: exception.Message); }
        catch (ConnectedServiceTypeNotFoundException exception)
        { return Results.Problem(statusCode: 404, title: "Connected Service type not found", detail: exception.Message); }
        catch (ConnectedServiceTypeInUseException exception)
        { return Results.Problem(statusCode: 409, title: "Connected Service type in use", detail: exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Results.Problem(statusCode: 409, title: "Resource version conflict", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Results.Problem(statusCode: 403, title: "Resource scope access denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception)
        { return Results.Problem(statusCode: 422, title: "Invalid resource scope", detail: exception.Message); }
        catch (InvalidDescendantUseGrantException exception)
        { return Results.Problem(statusCode: 422, title: "Invalid use grant", detail: exception.Message); }
        catch (Exception exception) when (exception is ConnectedServiceValidationException
            or ConnectedServiceTypeValidationException
            or ArgumentException or InvalidOperationException or FormatException)
        { return Results.Problem(statusCode: 422, title: "Invalid Connected Service operation", detail: exception.Message); }
    }
}
