using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IParametersClient
{
    Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken);
    Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
}

public sealed class ParametersApiClient(HttpClient httpClient) : IParametersClient
{
    public async Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ParameterResource[]>(httpClient, "api/parameters", token);

    public async Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ResourceScopeTargetResponse[]>(httpClient,
            $"api/resource-scopes/targets?kind={Uri.EscapeDataString(ParameterResourceKinds.Parameter)}", token);

    public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Get, Path(name, scopeRef), null, null, token);

    public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, "api/parameters", JsonContent.Create(request), null, token);

    public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef,
        PutParameterRequest request, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Put, Path(name, scopeRef), JsonContent.Create(request), etag, token);

    public async Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(name, scopeRef));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
    }

    public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
        ApiResponse.ReadAsync<ParameterUsagesResponse>(httpClient,
            $"api/parameters/{Uri.EscapeDataString(name)}/usages?scopeRef={Uri.EscapeDataString(scopeRef.Value)}", token);

    private async Task<ResourceSnapshot<ParameterResource>> ReadSnapshotAsync(HttpMethod method, string path,
        HttpContent? content, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync<ParameterResource>(token)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Parameter resource.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag))
            throw new AgentstrationApiException("Agentstration API did not return the Parameter ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string Path(string name, ResourceScopeRef scopeRef) =>
        $"api/parameters/{Uri.EscapeDataString(name)}?scopeRef={Uri.EscapeDataString(scopeRef.Value)}";
}
