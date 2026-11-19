using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Agentstration.SystemGraph.Api;

public static class SystemGraphApiModule
{
    public static IServiceCollection AddSystemGraphApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSystemGraphApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system-graph", QueryAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<SystemGraphPage>()
            .WithSummary("Query a bounded authorized semantic SystemGraph slice");
        endpoints.MapGet("/api/system-graph/stream", StreamAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<SystemGraphStreamFrame>(contentType: "application/x-ndjson")
            .WithSummary("Stream bounded authorized provisional SystemGraph observations")
            .WithDescription("Finite newline-delimited JSON batches for progressive exploration. A root follows authorized outgoing dependencies across namespaces, with maximumDepth from 1 to 32 (default 16), plus direct consumers of the root. Optional direction=dependencies or direction=usages requires an exact root and follows outgoing or incoming relations respectively, excluding lifecycle publication/membership edges. Original edge directions and authorization are preserved. Namespace filters the inventory or the root itself. Not an atomic snapshot or a reconciliation read set. Errors clear the observation; no resume cursor is issued.");
        endpoints.MapGet("/api/system-graph/impact", ImpactAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<SystemGraphImpactResult>()
            .WithSummary("Explain bounded authorized reverse dependency paths");
        endpoints.MapGet("/api/system-graph/changes", ChangesAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .Produces<SystemGraphChangeResult>()
            .WithSummary("Compare two authorized generations or publications in one observation");
        endpoints.MapGet("/api/system-graph/coverage", async (ISystemGraphImpactService service, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.GetCoverageAsync(ct)); }
            catch (SystemGraphException exception) { return GraphProblem(exception); }
        }).RequireAuthorization(AgentstrationPolicies.CanReadResources).Produces<IReadOnlyList<SystemGraphContributorCoverage>>()
            .WithSummary("Describe the currently authorized SystemGraph contributor coverage");
        return endpoints;
    }

    private static async Task<IResult> StreamAsync(string? @namespace, string? root, int? maximumNodes, int? maximumDepth, string? direction,
        ISystemGraphStreamService service, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        try
        {
            ResourceNamespace? ns = @namespace is null ? null : ResourceNamespace.Parse(@namespace);
            var traversal = direction?.ToLowerInvariant() switch
            {
                null or "context" => SystemGraphDirection.Context,
                "dependencies" => SystemGraphDirection.Dependencies,
                "usages" => SystemGraphDirection.Usages,
                _ => throw new SystemGraphException("system_graph_query_invalid")
            };
            await using var frames = service.ReadStreamAsync(new(ns, root, maximumNodes ?? 10000, maximumDepth ?? 16, traversal), ct).GetAsyncEnumerator(ct);
            // Initial authorization and validation precede the first response byte.
            if (!await frames.MoveNextAsync()) return Results.Empty;
            response.ContentType = "application/x-ndjson; charset=utf-8";
            response.Headers["X-Accel-Buffering"] = "no";
            response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            do
            {
                await JsonSerializer.SerializeAsync(response.Body, frames.Current, options, ct);
                await response.WriteAsync("\n", ct);
                await response.Body.FlushAsync(ct);
            } while (await frames.MoveNextAsync());
            return Results.Empty;
        }
        catch (ArgumentException) when (!response.HasStarted) { return Results.Problem(statusCode: 400, title: "system_graph_query_invalid"); }
        catch (SystemGraphException exception) when (!response.HasStarted) { return GraphProblem(exception); }
    }

    private static async Task<IResult> ChangesAsync(string from, string to, string? @namespace, int? maximumDepth, int? maximumPaths,
        ISystemGraphImpactService service, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        try
        {
            ResourceNamespace? ns = @namespace is null ? null : ResourceNamespace.Parse(@namespace);
            return Results.Ok(await service.CompareAsync(new(from, to, ns, maximumDepth ?? 16, maximumPaths ?? 200), ct));
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "system_graph_query_invalid"); }
        catch (SystemGraphException exception) { return GraphProblem(exception); }
    }

    private static IResult GraphProblem(SystemGraphException exception) => Results.Problem(statusCode: exception.Code switch
    {
        "system_graph_forbidden" => 403,
        "system_graph_unavailable" => 503,
        "system_graph_changed" => 409,
        "system_graph_limit_exceeded" => 422,
        _ => 400
    }, title: exception.Code);

    private static async Task<IResult> ImpactAsync(string root, string? @namespace, int? maximumDepth, int? maximumPaths,
        ISystemGraphImpactService service, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";
        try
        {
            ResourceNamespace? ns = @namespace is null ? null : ResourceNamespace.Parse(@namespace);
            return Results.Ok(await service.AnalyzeAsync(new(root, ns, maximumDepth ?? 16, maximumPaths ?? 200), ct));
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "system_graph_query_invalid"); }
        catch (SystemGraphException exception) { return GraphProblem(exception); }
    }

    private static async Task<IResult> QueryAsync(string? @namespace, string? root, int? limit, string? cursor,
        ISystemGraphQueryService service, HttpResponse response, CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        try
        {
            ResourceNamespace? ns = @namespace is null ? null : ResourceNamespace.Parse(@namespace);
            return Results.Ok(await service.QueryAsync(new(ns, root, limit ?? 100, cursor), cancellationToken));
        }
        catch (ArgumentException)
        { return Results.Problem(statusCode: 400, title: "system_graph_query_invalid"); }
        catch (SystemGraphException exception) { return GraphProblem(exception); }
    }
}
