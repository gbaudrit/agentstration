using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IToolsClient
{
    Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string group, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string group, string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken);
    Task<ToolConnectionTestResponse> TestAsync(string group, string name, CancellationToken cancellationToken);
    Task<ToolDiscoveryDiffResponse> RefreshAsync(string group, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? group, string? provider = null, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(string group, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string group, string name, bool enabled, string? etag, CancellationToken cancellationToken);
}

public sealed class ToolsApiClient(HttpClient httpClient) : IToolsClient
{
    public async Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolProviderResource>>(httpClient, "api/toolproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string group, string name, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Get, ProviderPath(group, name), null, null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Post, "api/toolproviders", JsonContent.Create(request), null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string group, string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Put, ProviderPath(group, name), JsonContent.Create(request), etag, cancellationToken);

    public async Task<ToolConnectionTestResponse> TestAsync(string group, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(group, name, "test"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolConnectionTestResponse>(cancellationToken))!;
    }

    public async Task<ToolDiscoveryDiffResponse> RefreshAsync(string group, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(group, name, "refresh"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolDiscoveryDiffResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? group, string? provider = null, CancellationToken cancellationToken = default)
    {
        var path = provider is null ? $"api/tools?resourceGroup={Uri.EscapeDataString(group ?? "default")}" : ProviderChildPath(group ?? "default", provider, "tools");
        return (await ApiResponse.ReadAsync<ValueResponse<ToolResource>>(httpClient, path, cancellationToken)).Value;
    }

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(string group, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ToolPath(group, name), cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string group, string name, bool enabled, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ToolPath(group, name) + "/enabled") { Content = JsonContent.Create(new SetToolEnabledRequest(enabled)) };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    private async Task<ResourceSnapshot<ToolProviderResource>> ReadProviderAsync(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new((await response.Content.ReadFromJsonAsync<ToolProviderResource>(cancellationToken))!, response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    private static async Task<ResourceSnapshot<ToolResource>> ReadToolAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new((await response.Content.ReadFromJsonAsync<ToolResource>(cancellationToken))!, response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    private static string ProviderPath(string group, string name) => $"api/toolproviders/{Uri.EscapeDataString(name)}?resourceGroup={Uri.EscapeDataString(group)}";
    private static string ProviderChildPath(string group, string name, string child) => $"api/toolproviders/{Uri.EscapeDataString(name)}/{child}?resourceGroup={Uri.EscapeDataString(group)}";
    private static string ToolPath(string group, string name) => $"api/tools/{Uri.EscapeDataString(name)}?resourceGroup={Uri.EscapeDataString(group)}";
}
