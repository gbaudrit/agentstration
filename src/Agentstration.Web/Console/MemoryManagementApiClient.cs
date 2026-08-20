using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Memory;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed record MemoryProviderTestResponse(string Provider, string Status, string? Detail = null);

public interface IMemoryManagementClient
{
    Task<IReadOnlyList<MemoryProviderResource>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryProfileResource>> GetProfilesAsync(CancellationToken cancellationToken);
    Task<MemoryProviderTestResponse> TestProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> GetRecordsAsync(ResourceNamespace @namespace, string providerName, int top, CancellationToken cancellationToken);
    Task DeleteRecordAsync(ResourceNamespace @namespace, string providerName, MemoryRecordId recordId, CancellationToken cancellationToken);
}

public sealed class MemoryManagementApiClient(HttpClient httpClient) : IMemoryManagementClient
{
    public async Task<IReadOnlyList<MemoryProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<MemoryProviderResource>>(httpClient, "api/memoryproviders", cancellationToken)).Value;

    public async Task<IReadOnlyList<MemoryProfileResource>> GetProfilesAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<MemoryProfileResource>>(httpClient, "api/memoryprofiles", cancellationToken)).Value;

    public async Task<MemoryProviderTestResponse> TestProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderPath(@namespace, name, "test"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MemoryProviderTestResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Memory provider status.", Guid.NewGuid().ToString("N"));
    }

    public async Task<IReadOnlyList<MemoryRecord>> GetRecordsAsync(ResourceNamespace @namespace, string providerName, int top, CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<MemoryRecordPage>(httpClient, $"{ProviderPath(@namespace, providerName, "records")}&top={Math.Clamp(top, 1, 100)}", cancellationToken)).Value;

    public async Task DeleteRecordAsync(ResourceNamespace @namespace, string providerName, MemoryRecordId recordId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"{ProviderPath(@namespace, providerName, $"records/{recordId.Value:D}")}", cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private static string ProviderPath(ResourceNamespace @namespace, string name, string child) =>
        $"api/memoryproviders/{Uri.EscapeDataString(name)}/{child}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
}
