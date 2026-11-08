using Agentstration.Api.Contracts;
using Agentstration.Web.Security;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage.Api;

internal static class StorageEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var types = endpoints.MapGroup("/api/storage-types").WithTags("Storage types");
        types.MapGet("/", async (string? scopeRef, int? skip, int? take, HttpRequest request,
            StorageTypeManagementService service, IResourceScopeOperations scopes, CancellationToken token) =>
            await Execute(async () =>
            {
                var (offset, limit) = Page(skip, take);
                var scope = Scope(scopeRef, scopes, StorageResourceKinds.StorageType);
                var values = await service.ListAsync(scope, offset, limit + 1, token);
                return Results.Ok(new PagedResponse<StorageTypeResource>(values.Take(limit).Select(v => v.Value).ToArray(),
                    Next(request, scope, offset, limit, values.Count > limit)));
            })).Produces<PagedResponse<StorageTypeResource>>().WithSummary("List visible Storage types")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        types.MapGet("/{name}", async (string name, string scopeRef, string? @namespace, HttpResponse response,
            StorageTypeManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var stored = await service.GetExactAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, token)
                    ?? throw new StorageException("storage_type_not_found", "Storage type not found.", 404);
                return Resource(stored, response);
            })).Produces<StorageTypeResource>().WithSummary("Get an exact Storage type")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        types.MapPost("/", async (CreateStorageTypeRequest body, HttpResponse response,
            StorageTypeManagementService service, CancellationToken token) => await Execute(async () =>
                Resource(await service.CreateAsync(new()
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
                    Metadata = new() { Name = body.Name, Namespace = body.Namespace ?? ResourceNamespace.Default },
                    ScopeRef = body.ScopeRef, Definition = body.Properties
                }, token), response, 201)))
            .Produces<StorageTypeResource>(201).WithSummary("Create a governed Storage type")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapPut("/{name}", async (string name, string scopeRef, string? @namespace, PutStorageTypeRequest body,
            HttpRequest request, HttpResponse response, StorageTypeManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.PutExactAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace),
                name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response)))
            .Produces<StorageTypeResource>().WithSummary("Update Storage type metadata or append immutable versions")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        types.MapDelete("/{name}", async (string name, string scopeRef, string? @namespace, HttpRequest request,
            StorageTypeManagementService service, CancellationToken token) => await Execute(async () =>
            {
                await service.DeleteExactAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name,
                    request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).Produces(204).WithSummary("Delete an unused user-owned Storage type")
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        var storages = endpoints.MapGroup("/api/storages").WithTags("Storages");
        storages.MapGet("/", async (string? scopeRef, int? skip, int? take, HttpRequest request,
            StorageManagementService service, IResourceScopeOperations scopes, CancellationToken token) =>
            await Execute(async () =>
            {
                var (offset, limit) = Page(skip, take);
                var scope = Scope(scopeRef, scopes, StorageResourceKinds.Storage);
                var values = await service.ListAsync(scope, offset, limit + 1, token);
                return Results.Ok(new PagedResponse<StorageResource>(values.Take(limit).Select(v => v.Value).ToArray(),
                    Next(request, scope, offset, limit, values.Count > limit)));
            })).Produces<PagedResponse<StorageResource>>().WithSummary("List visible scoped Storages")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        storages.MapGet("/{name}", async (string name, string scopeRef, string? @namespace, HttpResponse response,
            StorageManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var stored = await service.GetExactAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, token)
                    ?? throw new StorageException("storage_not_found", "Storage not found.", 404);
                return Resource(stored, response);
            })).Produces<StorageResource>().WithSummary("Get an exact Storage and bounded diagnostics")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        storages.MapGet("/{name}/usages", async (string name, string scopeRef, string? @namespace,
            StorageManagementService service, CancellationToken token) => await Execute(async () =>
                Results.Ok(new ValueResponse<StorageUsage>(await service.GetUsagesAsync(ResourceScopeRef.Parse(scopeRef),
                    Ns(@namespace), name, token)))))
            .Produces<ValueResponse<StorageUsage>>().WithSummary("List Storage dependencies")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        storages.MapPost("/", async (CreateStorageRequest body, HttpResponse response,
            StorageManagementService service, CancellationToken token) => await Execute(async () =>
                Resource(await service.CreateAsync(new()
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.Storage,
                    Metadata = new() { Name = body.Name, Namespace = body.Namespace ?? ResourceNamespace.Default },
                    ScopeRef = body.ScopeRef, Definition = body.Properties
                }, token), response, 201)))
            .Produces<StorageResource>(201).WithSummary("Create a Storage with explicit export mappings")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        storages.MapPut("/{name}", async (string name, string scopeRef, string? @namespace, PutStorageRequest body,
            HttpRequest request, HttpResponse response, StorageManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.PutExactAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace),
                name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response)))
            .Produces<StorageResource>().WithSummary("Update a Storage using If-Match")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        storages.MapPost("/{name}/reconcile", async (string name, string scopeRef, string? @namespace,
            ReconcileStorageRequest body, HttpRequest request, HttpResponse response, StorageManagementService service,
            CancellationToken token) => await Execute(async () => Resource(await service.ReconcileAsync(
                ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, body.Properties,
                request.Headers.IfMatch.FirstOrDefault(), token), response)))
            .Produces<StorageResource>().WithSummary("Observe drift or explicitly replace the complete Storage binding")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        storages.MapPost("/{name}/deactivate", async (string name, string scopeRef, string? @namespace,
            HttpRequest request, HttpResponse response, StorageManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(await service.DeactivateAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace),
                name, request.Headers.IfMatch.FirstOrDefault(), token), response)))
            .Produces<StorageResource>().WithSummary("Safely deactivate an unused Storage")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static ResourceScopeRef Scope(string? scope, IResourceScopeOperations scopes, string kind) =>
        string.IsNullOrWhiteSpace(scope) ? scopes.DefaultScopeRef(kind) : ResourceScopeRef.Parse(scope);
    private static ResourceNamespace Ns(string? ns) => ResourceNamespace.Parse(ns ?? ResourceNamespace.DefaultValue);
    private static (int Skip, int Take) Page(int? skip, int? take)
    {
        if (skip is < 0 or > 1_000_000 || take is < 1 or > 100)
            throw new StorageException("storage_page_invalid", "skip must be 0 to 1000000 and take 1 to 100.");
        return (skip ?? 0, take ?? 50);
    }
    private static string? Next(HttpRequest request, ResourceScopeRef scope, int skip, int take, bool more) =>
        more ? $"{request.Path}?scopeRef={Uri.EscapeDataString(scope.Value)}&skip={skip + take}&take={take}" : null;
    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status = 200) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (StorageException exception)
        { return Problem(exception.StatusCode, exception.Code, exception.Message); }
        catch (ResourceConcurrencyException)
        { return Problem(409, "storage_version_conflict", "The resource version has changed."); }
        catch (ResourceGovernanceViolationException exception)
        { return Problem(409, exception.Code, "Resource governance prevents this operation."); }
        catch (ResourceScopeAccessDeniedException)
        { return Problem(403, "storage_access_denied", "Resource scope access denied."); }
        catch (Exception exception) when (exception is ArgumentException or FormatException
            or ResourceScopePolicyException or InvalidDescendantUseGrantException)
        { return Problem(422, "storage_request_invalid", "Invalid Storage request or scope."); }
    }
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
