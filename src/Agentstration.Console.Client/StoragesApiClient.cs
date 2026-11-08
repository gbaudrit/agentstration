using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Web.Console;

public interface IStoragesClient
{
    Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token);
    Task<IReadOnlyList<StorageResource>> GetStoragesAsync(ResourceScopeRef scope, CancellationToken token);
    Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token);
    Task<ResourceSnapshot<StorageResource>> GetAsync(ScopedResourceAddress address, CancellationToken token);
    Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress address, CancellationToken token);
    Task<ResourceSnapshot<StorageResource>> CreateAsync(CreateStorageRequest body, CancellationToken token);
    Task<ResourceSnapshot<StorageResource>> UpdateAsync(ScopedResourceAddress address, PutStorageRequest body, string etag, CancellationToken token);
    Task<ResourceSnapshot<StorageResource>> ReconcileAsync(ScopedResourceAddress address, ReconcileStorageRequest body, string etag, CancellationToken token);
    Task<ResourceSnapshot<StorageResource>> DeactivateAsync(ScopedResourceAddress address, string etag, CancellationToken token);
}

public sealed class StoragesApiClient(HttpClient httpClient) : IStoragesClient
{
    public Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token) =>
        ReadPagesAsync<StorageTypeResource>("api/storage-types", scope, token);

    public Task<IReadOnlyList<StorageResource>> GetStoragesAsync(ResourceScopeRef scope, CancellationToken token) =>
        ReadPagesAsync<StorageResource>("api/storages", scope, token);

    public async Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ResourceScopeTargetResponse[]>(httpClient, "api/resource-scopes/targets?kind=Storage", token);

    public Task<ResourceSnapshot<StorageResource>> GetAsync(ScopedResourceAddress address, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Get, Path(address), null, null, token);

    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress address, CancellationToken token) =>
        (await ApiResponse.ReadAsync<ValueResponse<StorageUsage>>(httpClient, Path(address, "usages"), token)).Value;

    public Task<ResourceSnapshot<StorageResource>> CreateAsync(CreateStorageRequest body, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, "api/storages", JsonContent.Create(body), null, token);

    public Task<ResourceSnapshot<StorageResource>> UpdateAsync(ScopedResourceAddress address, PutStorageRequest body, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Put, Path(address), JsonContent.Create(body), etag, token);

    public Task<ResourceSnapshot<StorageResource>> ReconcileAsync(ScopedResourceAddress address, ReconcileStorageRequest body, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, Path(address, "reconcile"), JsonContent.Create(body), etag, token);

    public Task<ResourceSnapshot<StorageResource>> DeactivateAsync(ScopedResourceAddress address, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, Path(address, "deactivate"), null, etag, token);

    private async Task<IReadOnlyList<T>> ReadPagesAsync<T>(string path, ResourceScopeRef scope, CancellationToken token)
    {
        var values = new List<T>();
        // Build each request locally: never forward API credentials to a returned absolute nextLink.
        for (var page = 0; page < 100; page++)
        {
            var result = await ApiResponse.ReadAsync<PagedResponse<T>>(httpClient,
                $"{path}?scopeRef={Uri.EscapeDataString(scope.Value)}&skip={page * 100}&take=100", token);
            values.AddRange(result.Value);
            if (string.IsNullOrEmpty(result.NextLink)) return values;
        }
        throw new AgentstrationApiException("Storage catalog exceeded the client page limit.", Guid.NewGuid().ToString("N"));
    }

    private async Task<ResourceSnapshot<StorageResource>> ReadSnapshotAsync(HttpMethod method, string path,
        HttpContent? content, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync<StorageResource>(token);
        var responseEtag = response.Headers.ETag?.ToString();
        if (value is null || string.IsNullOrWhiteSpace(responseEtag))
            throw new AgentstrationApiException("Storage response is missing its resource or ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string Path(ScopedResourceAddress address, string? action = null) =>
        $"api/storages/{Uri.EscapeDataString(address.Name)}{(action is null ? "" : $"/{action}")}?scopeRef={Uri.EscapeDataString(address.ScopeRef.Value)}&namespace={Uri.EscapeDataString(address.Namespace.Value)}";
}
