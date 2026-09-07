using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IResourceScopeInventoryClient
{
    Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken);
}

public sealed class ResourceScopeInventoryApiClient(HttpClient httpClient) : IResourceScopeInventoryClient
{
    public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ResourceScopeInventoryResponse>(httpClient, "api/resource-scopes", cancellationToken);
}
