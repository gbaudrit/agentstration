using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;

namespace Agentstration.Web.Console;

public interface IToolsClient
{
    Task<ToolOperationalStatus> GetOperationalStateAsync(ToolResource tool, CancellationToken token) => Task.FromResult(new ToolOperationalStatus(tool.Uid, tool.Definition.Enabled, null));
    Task<ToolProviderAdministration> GetProviderAdministrationAsync(ToolProviderResource provider, CancellationToken token) => Task.FromResult(new ToolProviderAdministration([], []));
    Task<IReadOnlyList<Agentstration.ResourceManagement.Contracts.ResourceRelatedAccessResult>> SetCollectionEnabledAsync(Guid parentUid, SetToolCollectionEnabledRequest request, CancellationToken token) => throw new NotSupportedException();
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, bool enabled, string? etag, CancellationToken token) => SetEnabledAsync(ns, name, enabled, etag, token);
    Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact-scope Tool Provider reads are not implemented by this client.");
    Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? UpdateProviderAsync(name, request, etag, cancellationToken) : throw new NotSupportedException("Namespaced Tool Provider updates are not implemented by this client.");
    Task<ToolConnectionTestResponse> TestAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? TestAsync(name, cancellationToken) : throw new NotSupportedException("Namespaced Tool Provider tests are not implemented by this client.");
    Task<ToolDiscoveryDiffResponse> RefreshAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? RefreshAsync(name, cancellationToken) : throw new NotSupportedException("Namespaced Tool Provider discovery is not implemented by this client.");
    Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken);
    Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolResource>> GetToolsAsync(ResourceScopeRef contextScope, string? provider = null, CancellationToken cancellationToken = default) =>
        GetToolsAsync(provider, cancellationToken);
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetToolAsync(name, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Tools.");
    Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetToolAsync(@namespace, name, cancellationToken);
    Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string? etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? SetEnabledAsync(name, enabled, etag, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Tools.");
    Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client does not support Tool execution.");
    Task<RunToolResponse> RunToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken) =>
        RunToolAsync(@namespace, name, request, cancellationToken);
}

public sealed class ToolsApiClient(HttpClient httpClient) : IToolsClient
{
    public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ReadProviderAsync(HttpMethod.Get, ProviderPath(name) + $"?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}&scopeRef={Uri.EscapeDataString(scopeRef.Value)}", null, null, cancellationToken);
    public async Task<ToolOperationalStatus> GetOperationalStateAsync(ToolResource tool, CancellationToken token)
    {
        using var response = await httpClient.GetAsync($"api/tools/{Uri.EscapeDataString(tool.Name)}/activation?namespace={Uri.EscapeDataString(tool.Namespace.Value)}&scopeRef={Uri.EscapeDataString(tool.ScopeRef!.Value.Value)}", token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<ToolOperationalStatus>(token) ?? throw new InvalidOperationException("The Tool operational state is empty.");
    }
    public async Task<ToolProviderAdministration> GetProviderAdministrationAsync(ToolProviderResource provider, CancellationToken token)
    {
        using var response = await httpClient.GetAsync($"api/toolproviders/{Uri.EscapeDataString(provider.Name)}/administration?resourceNamespace={Uri.EscapeDataString(provider.Namespace.Value)}&scopeRef={Uri.EscapeDataString(provider.ScopeRef!.Value.Value)}", token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<ToolProviderAdministration>(token) ?? throw new InvalidOperationException("The Tool provider administration response is empty.");
    }
    public async Task<IReadOnlyList<Agentstration.ResourceManagement.Contracts.ResourceRelatedAccessResult>> SetCollectionEnabledAsync(Guid parentUid, SetToolCollectionEnabledRequest body, CancellationToken token)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/tools/administration/{parentUid:D}/enabled", body, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<Agentstration.ResourceManagement.Contracts.ResourceRelatedAccessResult[]>(token) ?? [];
    }
    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, bool enabled, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/tools/{Uri.EscapeDataString(name)}/enabled?namespace={Uri.EscapeDataString(ns.Value)}&scopeRef={Uri.EscapeDataString(scope.Value)}") { Content = JsonContent.Create(new SetToolEnabledRequest(enabled)) };
        if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await ReadToolAsync(response, token);
    }

    public async Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ToolProviderResource>>(httpClient, "api/toolproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Get, ProviderPath(name), null, null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Post, "api/toolproviders", JsonContent.Create(request), null, cancellationToken);
    public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => ReadProviderAsync(HttpMethod.Put, ProviderPath(name), JsonContent.Create(request), etag, cancellationToken);

    public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) =>
        ReadProviderAsync(HttpMethod.Put, ProviderPath(name) + ProviderCoordinates(scopeRef, @namespace), JsonContent.Create(request), etag, cancellationToken);

    public async Task<ToolConnectionTestResponse> TestAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(name, "test") + ProviderCoordinates(scopeRef, @namespace), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolConnectionTestResponse>(cancellationToken))!;
    }

    public async Task<ToolDiscoveryDiffResponse> RefreshAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ProviderChildPath(name, "refresh") + ProviderCoordinates(scopeRef, @namespace), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolDiscoveryDiffResponse>(cancellationToken))!;
    }

    private static string ProviderCoordinates(ResourceScopeRef scopeRef, ResourceNamespace @namespace) =>
        $"?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}&scopeRef={Uri.EscapeDataString(scopeRef.Value)}";

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

    public async Task<IReadOnlyList<ToolResource>> GetToolsAsync(ResourceScopeRef contextScope, string? provider = null, CancellationToken cancellationToken = default)
    {
        var path = (provider is null ? "api/tools" : ProviderChildPath(provider, "tools"))
            + $"?scopeRef={Uri.EscapeDataString(contextScope.Value)}";
        return (await ApiResponse.ReadAsync<ValueResponse<ToolResource>>(httpClient, path, contextScope, cancellationToken)).Value;
    }

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken)
        => await GetToolAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ToolPath(@namespace, name), cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ToolPath(@namespace, name, scopeRef), cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken)
        => await SetEnabledAsync(ResourceNamespace.Default, name, enabled, etag, cancellationToken);

    public async Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ToolPath(@namespace, name, child: "enabled")) { Content = JsonContent.Create(new SetToolEnabledRequest(enabled)) };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadToolAsync(response, cancellationToken);
    }

    public Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken) =>
        RunToolCoreAsync(@namespace, name, request, null, cancellationToken);

    public Task<RunToolResponse> RunToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken) =>
        RunToolCoreAsync(@namespace, name, request, scopeRef, cancellationToken);

    private async Task<RunToolResponse> RunToolCoreAsync(ResourceNamespace @namespace, string name, RunToolRequest request,
        ResourceScopeRef? scopeRef, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(ToolPath(@namespace, name, scopeRef, "run"), request, cancellationToken);
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
    private static string ToolPath(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef = null, string? child = null)
    {
        var path = $"api/tools/{Uri.EscapeDataString(name)}";
        if (!string.IsNullOrWhiteSpace(child)) path += $"/{child}";
        var query = new List<string>();
        if (!@namespace.IsDefault) query.Add($"namespace={Uri.EscapeDataString(@namespace.Value)}");
        if (scopeRef is { } scope) query.Add($"scopeRef={Uri.EscapeDataString(scope.Value)}");
        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }
}
