using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface ISecretsClient
{
    Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken);
    Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken);
    Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken);
    Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken);
    Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken);
    Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken);
}

public sealed class SecretsApiClient(HttpClient httpClient) : ISecretsClient
{
    public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken token) => ReadListAsync<VaultResponse>("api/vaults", token);
    public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken token) => ReadSnapshotAsync<VaultResponse>(HttpMethod.Get, VaultPath(name), null, null, token);
    public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken token) => ReadSnapshotAsync<VaultResource>(HttpMethod.Post, "api/vaults", JsonContent.Create(request), null, token);
    public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken token) => ReadSnapshotAsync<VaultResource>(HttpMethod.Put, VaultPath(name), JsonContent.Create(request), etag, token);
    public Task DeleteVaultAsync(string name, string etag, CancellationToken token) => DeleteAsync(VaultPath(name), etag, token);
    public async Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken token) { using var response = await httpClient.PostAsync($"{VaultPath(name)}/initialize", null, token); await ApiResponse.EnsureSuccessAsync(response, token); return await response.Content.ReadFromJsonAsync<VaultInitializationResponse>(token) ?? throw new AgentstrationApiException("Agentstration API returned an empty Vault initialization response.", Guid.NewGuid().ToString("N")); }
    public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken token) => ReadListAsync<SecretResponse>("api/secrets", token);
    public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken token) => ReadSnapshotAsync<SecretResponse>(HttpMethod.Get, SecretPath(name), null, null, token);
    public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken token) => ReadSnapshotAsync<SecretResource>(HttpMethod.Post, "api/secrets", JsonContent.Create(request), null, token);
    public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken token) => ReadSnapshotAsync<SecretResource>(HttpMethod.Put, SecretPath(name), JsonContent.Create(request), etag, token);
    public async Task SetSecretValueAsync(string name, string value, CancellationToken token) { using var response = await httpClient.PutAsJsonAsync(SecretPath(name, "value"), new SetSecretValueRequest(value), token); await ApiResponse.EnsureSuccessAsync(response, token); }
    public async Task DeleteSecretValueAsync(string name, CancellationToken token) { using var response = await httpClient.DeleteAsync(SecretPath(name, "value"), token); await ApiResponse.EnsureSuccessAsync(response, token); }
    public Task DeleteSecretAsync(string name, string etag, CancellationToken token) => DeleteAsync(SecretPath(name), etag, token);
    public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken token) => ApiResponse.ReadAsync<SecretUsagesResponse>(httpClient, SecretPath(name, "usages"), token);

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(string path, CancellationToken token) => await ApiResponse.ReadAsync<T[]>(httpClient, path, token);
    private async Task<ResourceSnapshot<T>> ReadSnapshotAsync<T>(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync<T>(token) ?? throw new AgentstrationApiException("Agentstration API returned an empty secret resource.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag)) throw new AgentstrationApiException("Agentstration API did not return the resource ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }
    private async Task DeleteAsync(string path, string etag, CancellationToken token) { using var request = new HttpRequestMessage(HttpMethod.Delete, path); request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag)); using var response = await httpClient.SendAsync(request, token); await ApiResponse.EnsureSuccessAsync(response, token); }
    private static string VaultPath(string name) => $"api/vaults/{Uri.EscapeDataString(name)}";
    private static string SecretPath(string name, string? child = null) { var path = $"api/secrets/{Uri.EscapeDataString(name)}"; return child is null ? path : $"{path}/{child}"; }
}
