using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ResourceScopeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/resource-scopes", async (ResourceScopeInventoryService service, CancellationToken token) =>
        {
            var scopes = await service.GetAsync(token);
            return Results.Ok(new ResourceScopeInventoryResponse(scopes.Select(scope => new ResourceScopeInventoryNodeResponse(
                scope.ScopeRef,
                scope.Kind,
                scope.DisplayName,
                scope.ParentScopeRef,
                scope.IsCurrent,
                scope.Resources.Select(resource => new ResourceScopeInventoryItemResponse(
                    resource.Uid,
                    resource.Namespace,
                    resource.Kind,
                    resource.Name,
                    resource.UpdatedAt)).ToArray())).ToArray()));
        })
            .Produces<ResourceScopeInventoryResponse>()
            .WithSummary("Visualize accessible resource scopes")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
    }
}
