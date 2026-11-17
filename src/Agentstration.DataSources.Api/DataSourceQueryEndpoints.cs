using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Web.Security;

namespace Agentstration.DataSources.Api.Internal;

internal static class DataSourceQueryEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/datasourcequeries");
        group.MapGet("/", (Guid sourceUid, string scopeRef, DataSourceQueryService service, CancellationToken ct) => Run(async () =>
            Results.Ok((await service.ListAsync(ResourceScopeRef.Parse(scopeRef), sourceUid, ct)).Select(v => v.Value).ToArray())))
            .Produces<IReadOnlyList<DataSourceQueryResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", (DataSourceQueryResource body, HttpResponse response, DataSourceQueryService service, CancellationToken ct) => Run(async () =>
        {
            var saved = await service.CreateAsync(body, ct); response.Headers.ETag = saved.ETag; return Results.Json(saved.Value, statusCode: 201);
        })).Produces<DataSourceQueryResource>(201).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/tools", (Guid sourceUid, string scopeRef, DataSourceQueryToolService service, CancellationToken ct) => Run(async () =>
            Results.Ok(await service.ListAsync(ResourceScopeRef.Parse(scopeRef), sourceUid, ct))))
            .Produces<IReadOnlyList<DataSourceQueryToolPublication>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}", (string name, string scopeRef, string? @namespace, HttpResponse response, DataSourceQueryService service, CancellationToken ct) => Run(async () =>
        {
            var saved = await service.GetAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name, ct);
            response.Headers.ETag = saved.ETag; return Results.Ok(saved.Value);
        })).Produces<DataSourceQueryResource>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/{name}", (string name, string scopeRef, string? @namespace, DataSourceQueryProperties body, HttpRequest request,
            HttpResponse response, DataSourceQueryService service, CancellationToken ct) => Run(async () =>
        {
            var saved = await service.UpdateAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name,
                body, request.Headers.IfMatch.FirstOrDefault(), ct);
            response.Headers.ETag = saved.ETag; return Results.Ok(saved.Value);
        })).Produces<DataSourceQueryResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/{name}/versions", (string name, string scopeRef, string? @namespace, DataSourceQueryService service, CancellationToken ct) => Run(async () =>
            Results.Ok(await service.VersionsAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name, ct))))
            .Produces<IReadOnlyList<DataSourceQueryVersionResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/{name}/versions", (string name, string scopeRef, string? @namespace, PublishDataSourceQueryRequest body,
            HttpRequest request, HttpResponse response, DataSourceQueryService service, DataSourceQueryToolService tools, CancellationToken ct) => Run(async () =>
        {
            var published = await service.PublishAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name,
                body.Version, request.Headers.IfMatch.FirstOrDefault(), ct);
            _ = await tools.EnsureGenericAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), ct);
            response.Headers.ETag = published.ETag; return Results.Ok(published);
        }))
            .Produces<DataSourceQueryVersionResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{name}/execute", (string name, string scopeRef, string? @namespace, string? version, DataSourceQueryExecutionRequest body,
            DataSourceQueryService service, CancellationToken ct) => Run(async () =>
        {
            var owner = await service.GetAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name, ct);
            return Results.Ok(await service.ExecuteAsync(new() { Query = new(name, owner.Value.ScopeRef, owner.Value.Namespace),
                QueryUid = owner.Value.Uid, Version = version }, body, null, null, ct));
        })).Produces<DataSourceQueryExecutionResult>().RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapPost("/{name}/tools", (string name, string scopeRef, string? @namespace, PublishDataSourceQueryToolRequest body,
            DataSourceQueryService service, DataSourceQueryToolService tools, CancellationToken ct) => Run(async () =>
        {
            var owner = await service.GetAsync(ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace), name, ct);
            return Results.Ok(await tools.PublishAsync(new() { Query = new(name, owner.Value.ScopeRef, owner.Value.Namespace), QueryUid = owner.Value.Uid }, body, ct));
        })).Produces<ResourceReference>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DataSourceValidationException e) { return Problem(e.Code, e.Code.EndsWith("not_found", StringComparison.Ordinal) ? 404
            : e.Code.EndsWith("access_denied", StringComparison.Ordinal) ? 403 : 422); }
        catch (DataSourceAcquisitionException e) { return Problem(e.Code, 422); }
        catch (StorageException e) { return Problem(e.Code, e.StatusCode); }
        catch (ResourceConcurrencyException) { return Problem("resource_version_conflict", 409); }
        catch (ResourceScopeAccessDeniedException) { return Problem("data_source_query_access_denied", 403); }
        catch (AuthorizationDeniedException) { return Problem("data_source_query_access_denied", 403); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return Problem("data_source_query_invalid", 422); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://agentstration.dev/problems/" + code, extensions: new Dictionary<string, object?> { ["code"] = code });
}
