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
            .Produces<ConnectedServiceTypeResource[]>()
            .WithSummary("List Connected Service types")
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
            })).Produces<ConnectedServiceTypeResource>().WithSummary("Get a Connected Service type")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
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
            .Produces<ConnectedServiceTypeResource>(StatusCodes.Status201Created)
            .WithSummary("Create a Connected Service type")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapPut("/{name}", async (string name, string scopeRef, string? @namespace,
            PutConnectedServiceTypeRequest body, HttpRequest request, HttpResponse response,
            ConnectedServiceTypeManagementService service, CancellationToken token) => await Execute(async () => Resource(
                await service.PutExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name, body.Properties,
                    request.Headers.IfMatch.FirstOrDefault(), token), response, StatusCodes.Status200OK)))
            .Produces<ConnectedServiceTypeResource>()
            .WithSummary("Update a Connected Service type")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapDelete("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpRequest request, ConnectedServiceTypeManagementService service, CancellationToken token) =>
            await Execute(async () =>
            {
                await service.DeleteExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete a Connected Service type")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        var operationTypes = endpoints.MapGroup("/api/connected-service-operation-types");
        operationTypes.MapGet("/", async (ConnectedServiceOperationTypeManagementService service,
            CancellationToken token) => Results.Ok((await service.ListAsync(token)).Select(value => value.Value)))
            .Produces<ConnectedServiceOperationTypeResource[]>()
            .WithSummary("List Connected Service operation types")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        operationTypes.MapGet("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpResponse response, ConnectedServiceOperationTypeManagementService service,
            CancellationToken token) => await Execute(async () =>
            {
                var ns = ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue);
                var stored = await service.GetExactAsync(ResourceScopeRef.Parse(scopeRef), ns, name, token)
                    ?? throw new ConnectedServiceOperationTypeNotFoundException(ns, name);
                response.Headers.ETag = stored.ETag;
                return Results.Ok(stored.Value);
            })).Produces<ConnectedServiceOperationTypeResource>()
            .WithSummary("Get a Connected Service operation type")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        operationTypes.MapPost("/", async (CreateConnectedServiceOperationTypeRequest body,
            HttpResponse response, ConnectedServiceOperationTypeManagementService service,
            CancellationToken token) => await Execute(async () => Resource(await service.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationType,
                Metadata = new() { Name = body.Name, Namespace = body.Namespace ?? ResourceNamespace.Default },
                ScopeRef = body.ScopeRef,
                Definition = body.Properties
            }, token), response, StatusCodes.Status201Created)))
            .Produces<ConnectedServiceOperationTypeResource>(StatusCodes.Status201Created)
            .WithSummary("Create a Connected Service operation type")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        operationTypes.MapPut("/{name}", async (string name, string scopeRef, string? @namespace,
            PutConnectedServiceOperationTypeRequest body, HttpRequest request, HttpResponse response,
            ConnectedServiceOperationTypeManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.PutExactAsync(ResourceScopeRef.Parse(scopeRef),
                ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name, body.Properties,
                request.Headers.IfMatch.FirstOrDefault(), token), response, StatusCodes.Status200OK)))
            .Produces<ConnectedServiceOperationTypeResource>()
            .WithSummary("Update a Connected Service operation type")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        operationTypes.MapDelete("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpRequest request, ConnectedServiceOperationTypeManagementService service,
            CancellationToken token) => await Execute(async () =>
            {
                await service.DeleteExactAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete a Connected Service operation type")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        var services = endpoints.MapGroup("/api/connected-services");
        services.MapGet("/", async (ConnectedServiceManagementService service, CancellationToken token) =>
            Results.Ok((await service.ListAsync(token)).Select(value => value.Value)))
            .Produces<ConnectedServiceResource[]>()
            .WithSummary("List Connected Services")
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
            })).Produces<ConnectedServiceResource>().WithSummary("Get a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
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
            .Produces<ConnectedServiceResource>(StatusCodes.Status201Created)
            .WithSummary("Create a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPut("/{name}", async (string name, string? scopeRef, PutConnectedServiceRequest body,
            HttpRequest request, HttpResponse response, ConnectedServiceManagementService service,
            CancellationToken token) => await Execute(async () => Resource(Scope(scopeRef) is { } scope
                ? await service.PutExactAsync(scope, name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token)
                : await service.PutAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .Produces<ConnectedServiceResource>()
            .WithSummary("Update a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPost("/{name}/reconcile", async (string name, string scopeRef, HttpRequest request,
            HttpResponse response, ConnectedServiceManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.ReconcileAsync(
                ResourceScopeRef.Parse(scopeRef), name, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .Produces<ConnectedServiceResource>()
            .WithSummary("Reconcile a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapPost("/{name}/deactivate", async (string name, string scopeRef, HttpRequest request,
            HttpResponse response, ConnectedServiceManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.DeactivateAsync(
                ResourceScopeRef.Parse(scopeRef), name, request.Headers.IfMatch.FirstOrDefault(), token),
                response, StatusCodes.Status200OK)))
            .Produces<ConnectedServiceResource>()
            .WithSummary("Deactivate a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        services.MapDelete("/{name}", async (string name, string scopeRef, HttpRequest request,
            ConnectedServiceManagementService service, CancellationToken token) => await Execute(async () =>
            {
                await service.DeleteAsync(ResourceScopeRef.Parse(scopeRef), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete a Connected Service")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        services.MapPost("/{name}/operation-discoveries", async (string name, string scopeRef,
            StartConnectedServiceOperationDiscoveryRequest body,
            HttpResponse response, ConnectedServiceOperationDiscoveryService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.StartAsync(ResourceScopeRef.Parse(scopeRef),
                name, body.CorrelationId, body.IdempotencyKey, token), response, StatusCodes.Status202Accepted)))
            .Produces<ConnectedServiceOperationDiscoveryResource>(StatusCodes.Status202Accepted)
            .WithSummary("Discover Connected Service operations")
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns, AgentstrationPolicies.CanWriteResources);
        services.MapGet("/{name}/operation-discoveries", async (string name, string scopeRef,
            ConnectedServiceOperationDiscoveryService service, CancellationToken token) =>
            await Execute(async () => Results.Ok(await service.ListAsync(
                ResourceScopeRef.Parse(scopeRef), name, token))))
            .Produces<ConnectedServiceOperationDiscoveryResource[]>()
            .WithSummary("List Connected Service operation discoveries")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        services.MapGet("/{name}/operations", async (string name, string scopeRef,
            ConnectedServiceOperationDiscoveryService service, CancellationToken token) =>
            await Execute(async () => Results.Ok(await service.ListOperationsAsync(
                ResourceScopeRef.Parse(scopeRef), name, token))))
            .Produces<ConnectedServiceOperationResource[]>()
            .WithSummary("List Connected Service operations")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);

        var discoveries = endpoints.MapGroup("/api/connected-service-operation-discoveries");
        discoveries.MapGet("/{id}", async (string id, string? @namespace,
            ConnectedServiceOperationDiscoveryService service, CancellationToken token) =>
            await Execute(async () => Results.Ok((await service.GetAsync(
                ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), id, token)).Value)))
            .Produces<ConnectedServiceOperationDiscoveryResource>()
            .WithSummary("Get a Connected Service operation discovery")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        discoveries.MapPost("/{id}/cancel", async (string id, string? @namespace,
            ConnectedServiceOperationDiscoveryService service, CancellationToken token) =>
            await Execute(async () => Results.Ok((await service.CancelAsync(
                ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), id, token)).Value)))
            .Produces<ConnectedServiceOperationDiscoveryResource>()
            .WithSummary("Cancel a Connected Service operation discovery")
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);

        var operations = endpoints.MapGroup("/api/connected-service-operations");
        operations.MapGet("/{name}", async (string name, string scopeRef, string? @namespace,
            HttpResponse response, ConnectedServiceOperationDiscoveryService service,
            CancellationToken token) => await Execute(async () =>
            {
                var stored = await service.GetOperationAsync(ResourceScopeRef.Parse(scopeRef),
                    ResourceNamespace.Parse(@namespace ?? ResourceNamespace.DefaultValue), name, token);
                if (stored is null) return Results.NotFound();
                response.Headers.ETag = stored.ETag;
                return Results.Ok(stored.Value);
            })).Produces<ConnectedServiceOperationResource>()
            .WithSummary("Get a Connected Service operation")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
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
        catch (ConnectedServiceInUseException exception)
        { return Results.Problem(statusCode: 409, title: "Connected Service in use", detail: exception.Message); }
        catch (ConnectedServiceTypeNotFoundException exception)
        { return Results.Problem(statusCode: 404, title: "Connected Service type not found", detail: exception.Message); }
        catch (ConnectedServiceTypeInUseException exception)
        { return Results.Problem(statusCode: 409, title: "Connected Service type in use", detail: exception.Message); }
        catch (ConnectedServiceOperationTypeNotFoundException exception)
        { return Results.Problem(statusCode: 404, title: "Connected Service operation type not found", detail: exception.Message); }
        catch (ConnectedServiceOperationTypeInUseException exception)
        { return Results.Problem(statusCode: 409, title: "Connected Service operation type in use", detail: exception.Message); }
        catch (ConnectedServiceOperationDiscoveryException exception)
        { return Results.Problem(statusCode: 422, title: exception.Code, detail: exception.Message); }
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
            or ConnectedServiceOperationTypeValidationException
            or ConnectedServiceOperationTypeRevisionException
            or ArgumentException or InvalidOperationException or FormatException)
        { return Results.Problem(statusCode: 422, title: "Invalid Connected Service operation", detail: exception.Message); }
    }
}
