using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IConnectedServicesClient
{
    Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
    Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetConnectedServiceAsync(name, scopeRef, cancellationToken)
            : throw new NotSupportedException("This client does not support non-default Connected Service namespaces.");

    Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetUsagesAsync(name, scopeRef, cancellationToken)
            : throw new NotSupportedException("This client does not support non-default Connected Service namespaces.");

    Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? UpdateConnectedServiceAsync(name, scopeRef, request, etag, cancellationToken)
            : throw new NotSupportedException("This client does not support non-default Connected Service namespaces.");

    Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? ReconcileAsync(name, scopeRef, etag, cancellationToken)
            : throw new NotSupportedException("This client does not support non-default Connected Service namespaces.");

    Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? DeactivateAsync(name, scopeRef, etag, cancellationToken)
            : throw new NotSupportedException("This client does not support non-default Connected Service namespaces.");

}

public sealed class ConnectedServicesApiClient(HttpClient httpClient) : IConnectedServicesClient
{
    public async Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ConnectedServiceTypeResource[]>(httpClient, "api/connected-service-types", token);

    public async Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ConnectedServiceResource[]>(httpClient, "api/connected-services", token);

    public async Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) =>
        await ApiResponse.ReadAsync<ResourceScopeTargetResponse[]>(httpClient,
            $"api/resource-scopes/targets?kind={Uri.EscapeDataString(ConnectedServiceResourceKinds.ConnectedService)}", token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Get, Path(name, scopeRef), null, null, token);

    public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken token) =>
        ApiResponse.ReadAsync<ConnectedServiceUsagesResponse>(httpClient, ActionPath(name, scopeRef, "usages"), token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, "api/connected-services", JsonContent.Create(request), null, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef,
        PutConnectedServiceRequest request, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Put, Path(name, scopeRef), JsonContent.Create(request), etag, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, ActionPath(name, scopeRef, "reconcile"), null, etag, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, ActionPath(name, scopeRef, "deactivate"), null, etag, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Get, Path(name, scopeRef, @namespace), null, null, token);

    public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, CancellationToken token) =>
        ApiResponse.ReadAsync<ConnectedServiceUsagesResponse>(httpClient, ActionPath(name, scopeRef, "usages", @namespace), token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, PutConnectedServiceRequest request, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Put, Path(name, scopeRef, @namespace), JsonContent.Create(request), etag, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, ActionPath(name, scopeRef, "reconcile", @namespace), null, etag, token);

    public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, ResourceNamespace @namespace, string etag, CancellationToken token) =>
        ReadSnapshotAsync(HttpMethod.Post, ActionPath(name, scopeRef, "deactivate", @namespace), null, etag, token);

    private async Task<ResourceSnapshot<ConnectedServiceResource>> ReadSnapshotAsync(HttpMethod method, string path,
        HttpContent? content, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync<ConnectedServiceResource>(token)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Connected Service resource.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag))
            throw new AgentstrationApiException("Agentstration API did not return the Connected Service ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string Path(string name, ResourceScopeRef scopeRef, ResourceNamespace? @namespace = null) =>
        $"api/connected-services/{Uri.EscapeDataString(name)}?scopeRef={Uri.EscapeDataString(scopeRef.Value)}" + NamespaceQuery(@namespace);

    private static string ActionPath(string name, ResourceScopeRef scopeRef, string action, ResourceNamespace? @namespace = null) =>
        $"api/connected-services/{Uri.EscapeDataString(name)}/{action}?scopeRef={Uri.EscapeDataString(scopeRef.Value)}" + NamespaceQuery(@namespace);

    private static string NamespaceQuery(ResourceNamespace? value) => value is { IsDefault: false } ns ? "&namespace=" + Uri.EscapeDataString(ns.Value) : string.Empty;
}
