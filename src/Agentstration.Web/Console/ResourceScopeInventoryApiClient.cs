using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IResourceScopeInventoryClient
{
    Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([]);
}

public sealed class ResourceScopeInventoryApiClient(HttpClient httpClient) : IResourceScopeInventoryClient
{
    public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ResourceScopeInventoryResponse>(httpClient, "api/resource-scopes", cancellationToken);

    public async Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<ResourceScopeTargetResponse[]>(httpClient, $"api/resource-scopes/targets?kind={Uri.EscapeDataString(kind)}", cancellationToken);
}
