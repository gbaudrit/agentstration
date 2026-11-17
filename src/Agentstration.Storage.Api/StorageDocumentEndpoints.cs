using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Storage.Api;

internal static class StorageDocumentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/storages/{name}/collections/{collectionUid:guid}").WithTags("Structured documents");
        group.MapPost("/tools/publish", async (string name, Guid collectionUid, string scopeRef, string? @namespace,
            StorageDocumentToolPublication body, StorageDocumentService service, IStorageDocumentToolExposure exposure, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await exposure.PublishAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), body, ct))))
            .Produces<IReadOnlyList<ResourceReference>>().WithSummary("Publish explicit governed collection Tools")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/capabilities", async (string name, Guid collectionUid, string scopeRef, string? @namespace,
            StorageDocumentService service, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await service.DescribeAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), ct))))
            .Produces<StorageDocumentDescription>().WithSummary("Inspect effective collection capabilities").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/documents/{id}", async (string name, Guid collectionUid, string id, string scopeRef, string? @namespace,
            string? revision, StorageDocumentService service, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await service.GetAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), id, revision, ct))))
            .Produces<StorageDocumentValue>().WithSummary("Execute a governed document operation").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/documents", async (string name, Guid collectionUid, string scopeRef, string? @namespace,
            StorageDocumentWrite body, StorageDocumentService service, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await service.PutAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), body, ct))))
            .Produces<StorageDocumentValue>().WithSummary("Execute a governed document operation").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/delete", async (string name, Guid collectionUid, string scopeRef, string? @namespace,
            StorageDocumentDelete body, StorageDocumentService service, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await service.DeleteAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), body, ct))))
            .Produces<StorageDocumentValue>().WithSummary("Execute a governed document operation").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/query", async (string name, Guid collectionUid, string scopeRef, string? @namespace,
            StorageDocumentQuery body, StorageDocumentService service, CancellationToken ct) => await Execute(async () =>
                Results.Ok(await service.QueryAsync(await service.ResolveTargetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, collectionUid, ct), body, ct))))
            .Produces<StorageDocumentPage>().WithSummary("Query a governed structured document collection").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }
    private static ResourceNamespace Ns(string? value) => string.IsNullOrWhiteSpace(value) ? ResourceNamespace.Default : ResourceNamespace.Parse(value);
    private static async Task<IResult> Execute(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (StorageException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, type: "https://agentstration.dev/problems/" + ex.Code, extensions: new Dictionary<string, object?> { ["code"] = ex.Code }); }
        catch (ArgumentException) { return Results.Problem(statusCode: 422, title: "Invalid structured Storage request."); }
    }
}
