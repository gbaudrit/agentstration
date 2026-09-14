namespace Agentstration.ResourceManagement.Contracts;

public interface IResourceScopeApiService
{
    Task<ResourceScopeInventoryResponse> GetInventoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceScopeTargetResponse>> ListTargetsAsync(string kind, CancellationToken cancellationToken);
}
