using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Security;

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
        return endpoints;
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
        catch (SystemGraphException exception)
        {
            var status = exception.Code switch
            {
                "system_graph_forbidden" => 403,
                "system_graph_unavailable" => 503,
                "system_graph_changed" => 409,
                "system_graph_limit_exceeded" => 422,
                _ => 400
            };
            return Results.Problem(statusCode: status, title: exception.Code);
        }
    }
}
