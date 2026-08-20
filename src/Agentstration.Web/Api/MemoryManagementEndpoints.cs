using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Memory;
using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Web;

public sealed record CreateMemoryProviderRequest(string Name, MemoryProviderProperties Properties, string? Namespace = null);
public sealed record PutMemoryProviderRequest(MemoryProviderProperties Properties);
public sealed record CreateMemoryProfileRequest(string Name, MemoryProfileProperties Properties, string? Namespace = null);
public sealed record PutMemoryProfileRequest(MemoryProfileProperties Properties);

public static class MemoryManagementEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationMemoryManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/memoryproviders");
        providers.MapGet("/", async (MemoryProviderManagementService service, CancellationToken token) => Results.Ok(new { value = (await service.ListAsync(token)).Select(item => item.Value) }));
        providers.MapGet("/{name}", async (string name, string? resourceNamespace, HttpResponse response, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await Required(service.GetAsync(Namespace(resourceNamespace), name, token), "memory_provider_not_found", name), response)));
        providers.MapPost("/", async (CreateMemoryProviderRequest body, HttpResponse response, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await service.CreateAsync(new MemoryProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.MemoryProvider,
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = Namespace(body.Namespace) },
                Definition = body.Properties
            }, token), response, 201)));
        providers.MapPut("/{name}", async (string name, string? resourceNamespace, PutMemoryProviderRequest body, HttpRequest request, HttpResponse response, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await service.PutAsync(Namespace(resourceNamespace), name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response)));
        providers.MapDelete("/{name}", async (string name, string? resourceNamespace, HttpRequest request, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () => { await service.DeleteAsync(Namespace(resourceNamespace), name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); }));
        providers.MapGet("/{name}/usages", async (string name, string? resourceNamespace, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () => Results.Ok(new { value = await service.GetUsagesAsync(Namespace(resourceNamespace), name, token) })));
        providers.MapGet("/{name}/status", async (string name, string? resourceNamespace, MemoryProviderManagementService service, CancellationToken token) =>
            await Result(async () =>
            {
                var provider = await Required(service.GetAsync(Namespace(resourceNamespace), name, token), "memory_provider_not_found", name);
                return Results.Ok(new { provider = provider.Value.Address.ToString(), status = "configured", integration = provider.Value.Definition.IntegrationKind.ToString().ToLowerInvariant() });
            }));
        providers.MapPost("/{name}/test", async (string name, string? resourceNamespace, MemoryProviderManagementService service, IMemoryRecordStoreResolver stores, ICurrentRequestContext requestContext, TimeProvider timeProvider, CancellationToken token) =>
            await Result(async () =>
            {
                var @namespace = Namespace(resourceNamespace);
                var provider = await Required(service.GetAsync(@namespace, name, token), "memory_provider_not_found", name);
                try
                {
                    var store = await stores.ResolveAsync(new WorkspaceId(requestContext.Current.WorkspaceId), new(provider.Value.Name, provider.Value.Namespace.Value), token);
                    await store.InitializeAsync(token);
                    _ = await store.ListAsync(new WorkspaceId(requestContext.Current.WorkspaceId), null, timeProvider.GetUtcNow(), 0, 1, token);
                    return Results.Ok(new { provider = provider.Value.Address.ToString(), status = "available" });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Ok(new { provider = provider.Value.Address.ToString(), status = "unavailable", detail = exception.Message });
                }
            }));

        var profiles = endpoints.MapGroup("/api/memoryprofiles");
        profiles.MapGet("/", async (MemoryProfileManagementService service, CancellationToken token) => Results.Ok(new { value = (await service.ListAsync(token)).Select(item => item.Value) }));
        profiles.MapGet("/{name}", async (string name, string? resourceNamespace, HttpResponse response, MemoryProfileManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await Required(service.GetAsync(Namespace(resourceNamespace), name, token), "memory_profile_not_found", name), response)));
        profiles.MapPost("/", async (CreateMemoryProfileRequest body, HttpResponse response, MemoryProfileManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await service.CreateAsync(new MemoryProfileResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.MemoryProfile,
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = Namespace(body.Namespace) },
                Definition = body.Properties
            }, token), response, 201)));
        profiles.MapPut("/{name}", async (string name, string? resourceNamespace, PutMemoryProfileRequest body, HttpRequest request, HttpResponse response, MemoryProfileManagementService service, CancellationToken token) =>
            await Result(async () => Resource(await service.PutAsync(Namespace(resourceNamespace), name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response)));
        profiles.MapDelete("/{name}", async (string name, string? resourceNamespace, HttpRequest request, MemoryProfileManagementService service, CancellationToken token) =>
            await Result(async () => { await service.DeleteAsync(Namespace(resourceNamespace), name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); }));
        profiles.MapGet("/{name}/usages", async (string name, string? resourceNamespace, MemoryProfileManagementService service, CancellationToken token) =>
            await Result(async () => Results.Ok(new { value = await service.GetUsagesAsync(Namespace(resourceNamespace), name, token) })));
        return endpoints;
    }

    private static ResourceNamespace Namespace(string? value) => ResourceNamespace.Parse(value);
    private static async Task<StoredResource<T>> Required<T>(Task<StoredResource<T>?> task, string code, string name) where T : Resource =>
        await task ?? throw new MemoryManagementException(code, $"Resource '{name}' was not found.");
    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status = 200) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }
    private static async Task<IResult> Result(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (MemoryManagementException exception) { return Results.Problem(statusCode: exception.Code.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 409, title: exception.Code, detail: exception.Message); }
        catch (ControlPlaneConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "concurrency_conflict", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }
}
