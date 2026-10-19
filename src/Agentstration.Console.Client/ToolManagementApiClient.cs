using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;

namespace Agentstration.Web.Console;

public interface IToolsClient
{
    Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken);
    Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken);
    Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetToolAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Tools.");
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string? etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? SetEnabledAsync(name, enabled, etag, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Tools.");
    Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client does not support Tool execution.");
}

public sealed class ToolsApiClient(HttpClient httpClient) : IToolsClient
{
    public async Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolProviderResource>>(httpClient, "api/toolproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Get, ProviderPath(name), null, null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Post, "api/toolproviders", JsonContent.Create(request), null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Put, ProviderPath(name), JsonContent.Create(request), etag, cancellationToken);

    public async Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(name, "test"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolConnectionTestResponse>(cancellationToken))!;
    }

    public async Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(name, "refresh"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolDiscoveryDiffResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default)
    {
        var path = provider is null ? "api/tools" : ProviderChildPath(provider, "tools");
        return (await ApiResponse.ReadAsync<ValueResponse<ToolResource>>(httpClient, path, cancellationToken)).Value;
    }

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken)
        => await GetToolAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ToolPath(@namespace, name), cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken)
        => await SetEnabledAsync(ResourceNamespace.Default, name, enabled, etag, cancellationToken);

    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ToolPath(@namespace, name, "enabled")) { Content = JsonContent.Create(new SetToolEnabledRequest(enabled)) };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public async Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(ToolPath(@namespace, name, "run"), request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<RunToolResponse>(cancellationToken))!;
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

    private static string ProviderPath(string name) => $"api/toolproviders/{Uri.EscapeDataString(name)}";
    private static string ProviderChildPath(string name, string child) => $"api/toolproviders/{Uri.EscapeDataString(name)}/{child}";
    private static string ToolPath(ResourceNamespace @namespace, string name, string? child = null)
    {
        var path = $"api/tools/{Uri.EscapeDataString(name)}";
        if (!string.IsNullOrWhiteSpace(child)) path += $"/{child}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }
}
