using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Hosting;

public sealed class ResourceScopeApiService(
    ResourceScopeInventoryService inventory,
    ResourceScopeOperationService operations) : IResourceScopeApiService
{
    public async Task<ResourceScopeInventoryResponse> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var scopes = await inventory.GetAsync(cancellationToken);
        return new(scopes.Select(scope => new ResourceScopeInventoryNodeResponse(
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
                resource.UpdatedAt)).ToArray())).ToArray());
    }

    public async Task<IReadOnlyList<ResourceScopeTargetResponse>> ListTargetsAsync(
        string kind,
        CancellationToken cancellationToken) =>
        (await operations.ListTargetsAsync(kind, AuthorizationPermissions.ResourcesWrite, cancellationToken))
            .Select(value => new ResourceScopeTargetResponse(value.ScopeRef, value.Kind, value.DisplayName, value.CanWrite))
            .ToArray();
}
