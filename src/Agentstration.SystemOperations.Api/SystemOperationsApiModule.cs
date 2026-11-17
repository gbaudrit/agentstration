using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.SystemOperations.Api;

public static class SystemOperationsApiModule
{
    public static IServiceCollection AddSystemOperationsApi(this IServiceCollection services)
    {
        services.AddSingleton<SystemOperationsSubscriptions>();
        services.Configure<HubOptions<SystemOperationsHub>>(options =>
        {
            options.StreamBufferCapacity = 1;
            options.MaximumParallelInvocationsPerClient = 1;
            options.MaximumReceiveMessageSize = 8192;
        });
        return services;
    }
    public static IEndpointRouteBuilder MapSystemOperationsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system-operations", async (string root, string? @namespace, int? windowMinutes, int? maximumRuns,
            ISystemOperationsService operations, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await operations.SnapshotAsync(new(root, @namespace is null ? null : ResourceNamespace.Parse(@namespace), windowMinutes ?? 15, maximumRuns ?? 50), ct)); }
            catch (SystemOperationsException error) { return Results.Problem(statusCode: error.Code == "system_operations_query_invalid" ? 400 : error.Code == "system_operations_forbidden" ? 403 : 409, title: error.Code); }
            catch (SystemGraphException error) { return Results.Problem(statusCode: error.Code == "system_graph_forbidden" ? 403 : 503, title: "system_operations_unavailable"); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "system_operations_query_invalid"); }
        }).Produces<SystemOperationalSnapshot>().WithSummary("Read a separately authorized bounded operational overlay")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapHub<SystemOperationsHub>("/hubs/system-operations", options =>
        {
            options.ApplicationMaxBufferSize = 65536;
            options.TransportMaxBufferSize = 65536;
        }).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        return endpoints;
    }
}
