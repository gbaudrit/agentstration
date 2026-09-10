using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface ISourceProvidersClient
{
    Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetSourceProviderAsync(@namespace, name, cancellationToken);
    Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) =>
        UpdateSourceProviderAsync(@namespace, name, request, etag, cancellationToken);
    Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken);
    Task DeleteSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) =>
        DeleteSourceProviderAsync(@namespace, name, etag, cancellationToken);
    Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<SourceProviderStatusResponse> GetStatusAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetStatusAsync(@namespace, name, cancellationToken);
    Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetUsagesAsync(@namespace, name, cancellationToken);
}

public sealed class SourceProvidersApiClient(HttpClient httpClient) : ISourceProvidersClient
{
    public async Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<SourceProviderSummaryResponse>>(httpClient, "api/sourceproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetSourceProviderAsync(ResourceScopeRef.Instance, @namespace, name, cancellationToken);

    public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Get, Path(scopeRef, @namespace, name), null, null, cancellationToken);

    public Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Post, "api/sourceproviders", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) =>
        UpdateSourceProviderAsync(ResourceScopeRef.Instance, @namespace, name, request, etag, cancellationToken);

    public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Put, Path(scopeRef, @namespace, name), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken)
    {
        await DeleteSourceProviderAsync(ResourceScopeRef.Instance, @namespace, name, etag, cancellationToken);
    }

    public async Task DeleteSourceProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(scopeRef, @namespace, name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetStatusAsync(ResourceScopeRef.Instance, @namespace, name, cancellationToken);

    public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<SourceProviderStatusResponse>(httpClient, ChildPath(scopeRef, @namespace, name, "status"), cancellationToken);

    public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        GetUsagesAsync(ResourceScopeRef.Instance, @namespace, name, cancellationToken);

    public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<SourceProviderUsagesResponse>(httpClient, ChildPath(scopeRef, @namespace, name, "usages"), cancellationToken);

    private async Task<ResourceSnapshot<SourceProviderResource>> ReadResourceAsync(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<SourceProviderResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Source Provider.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag))
            throw new AgentstrationApiException("Agentstration API did not return the Source Provider ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string Path(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        $"api/sourceproviders/{Uri.EscapeDataString(name)}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}&scopeRef={Uri.EscapeDataString(scopeRef.Value)}";
    private static string ChildPath(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string child) =>
        $"api/sourceproviders/{Uri.EscapeDataString(name)}/{child}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}&scopeRef={Uri.EscapeDataString(scopeRef.Value)}";
}
