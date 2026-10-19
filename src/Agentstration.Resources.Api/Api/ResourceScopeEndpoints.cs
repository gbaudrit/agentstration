using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ResourceScopeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/resource-scopes", async (IResourceScopeApiService service, CancellationToken token) =>
            Results.Ok(await service.GetInventoryAsync(token)))
            .Produces<ResourceScopeInventoryResponse>()
            .WithSummary("Visualize accessible resource scopes")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/resource-scopes/targets", async (string kind, IResourceScopeApiService service, CancellationToken token) =>
            Results.Ok(await service.ListTargetsAsync(kind, token)))
            .Produces<IEnumerable<ResourceScopeTargetResponse>>()
            .WithSummary("List writable resource scope targets")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
    }
}
