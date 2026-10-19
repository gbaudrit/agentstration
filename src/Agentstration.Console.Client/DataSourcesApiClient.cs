using System.Net.Http.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IDataSourcesClient
{
    Task<IReadOnlyList<DataSourceResource>> GetAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceResource>> CreateAsync(CreateDataSourceRequest request,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        PutDataSourceRequest request, string etag, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag,
        CancellationToken cancellationToken = default);
    Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name,
        ResourceScopeRef sourceScope,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace ns, string name,
        ResourceScopeRef sourceScope, StartDataSourceAcquisitionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDataSourceProfilesClient
{
    Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request,
        CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag,
        CancellationToken cancellationToken = default);
}

public sealed class DataSourcesApiClient(HttpClient httpClient) : IDataSourcesClient
{
    public Task<IReadOnlyList<DataSourceResource>> GetAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<DataSourceResource>>(httpClient, "api/datasources", cancellationToken);
    public Task<ResourceSnapshot<DataSourceResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default) => GetResourceAsync<DataSourceResource>(Path(ns, name, scope), cancellationToken);
    public Task<ResourceSnapshot<DataSourceResource>> CreateAsync(CreateDataSourceRequest request,
        CancellationToken cancellationToken = default) => SendAsync<DataSourceResource>(HttpMethod.Post,
            "api/datasources", request, null, cancellationToken);
    public Task<ResourceSnapshot<DataSourceResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        PutDataSourceRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendAsync<DataSourceResource>(HttpMethod.Put, Path(ns, name, scope), request, etag, cancellationToken);
    public async Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(ns, name, scope));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
    public Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default) => ApiResponse.ReadAsync<DataSourceReadiness>(httpClient,
            ChildPath(ns, name, scope, "readiness"), cancellationToken);
    public Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name,
        ResourceScopeRef sourceScope,
        CancellationToken cancellationToken = default) => ApiResponse.ReadAsync<IReadOnlyList<DataSourceAcquisitionResource>>(
            httpClient, ChildPath(ns, name, sourceScope, "acquisitions"), cancellationToken);
    public Task<ResourceSnapshot<DataSourceAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace ns, string name,
        ResourceScopeRef sourceScope, StartDataSourceAcquisitionRequest request,
        CancellationToken cancellationToken = default) => SendAsync<DataSourceAcquisitionResource>(HttpMethod.Post,
            ChildPath(ns, name, sourceScope, "acquisitions"), request, null, cancellationToken);

    private async Task<ResourceSnapshot<T>> GetResourceAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await SnapshotAsync<T>(response, cancellationToken);
    }
    private async Task<ResourceSnapshot<T>> SendAsync<T>(HttpMethod method, string path, object body, string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await SnapshotAsync<T>(response, cancellationToken);
    }
    private static async Task<ResourceSnapshot<T>> SnapshotAsync<T>(HttpResponseMessage response, CancellationToken token) =>
        new((await response.Content.ReadFromJsonAsync<T>(token))!, response.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("Missing ETag."));
    private static string Path(ResourceNamespace ns, string name, ResourceScopeRef scope) =>
        $"api/datasources/{Uri.EscapeDataString(name)}?namespace={Uri.EscapeDataString(ns.Value)}&scopeRef={Uri.EscapeDataString(scope.Value)}";
    private static string ChildPath(ResourceNamespace ns, string name, ResourceScopeRef? scope, string child) =>
        $"api/datasources/{Uri.EscapeDataString(name)}/{child}?namespace={Uri.EscapeDataString(ns.Value)}"
        + (scope is null ? string.Empty : $"&scopeRef={Uri.EscapeDataString(scope.Value.Value)}");
}

public sealed class DataSourceProfilesApiClient(HttpClient httpClient) : IDataSourceProfilesClient
{
    public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<DataSourceProfileResource>>(httpClient, "api/datasourceprofiles", cancellationToken);
    public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        CancellationToken cancellationToken = default) => GetResourceAsync<DataSourceProfileResource>(Path(ns, name, scope), cancellationToken);
    public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<DataSourceProfileRevisionResource>>(httpClient,
            ChildPath(ns, name, scope, "revisions"), cancellationToken);
    public Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request,
        CancellationToken cancellationToken = default) => SendAsync<DataSourceProfileResource>(HttpMethod.Post,
            "api/datasourceprofiles", request, null, cancellationToken);
    public Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope,
        PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendAsync<DataSourceProfileResource>(HttpMethod.Put, Path(ns, name, scope), request, etag, cancellationToken);
    public Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<DataSourceProfileRevisionResource>(HttpMethod.Post, ChildPath(ns, name, scope, "revisions"),
            request, null, cancellationToken);
    public Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name,
        ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag,
        CancellationToken cancellationToken = default) => SendAsync<DataSourceProfileResource>(HttpMethod.Put,
            ChildPath(ns, name, scope, "active-revision"), request, etag, cancellationToken);
    public async Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(ns, name, scope));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
    private async Task<ResourceSnapshot<T>> GetResourceAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await SnapshotAsync<T>(response, cancellationToken);
    }
    private async Task<ResourceSnapshot<T>> SendAsync<T>(HttpMethod method, string path, object body, string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await SnapshotAsync<T>(response, cancellationToken);
    }
    private static async Task<ResourceSnapshot<T>> SnapshotAsync<T>(HttpResponseMessage response, CancellationToken token) =>
        new((await response.Content.ReadFromJsonAsync<T>(token))!, response.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("Missing ETag."));
    private static string Path(ResourceNamespace ns, string name, ResourceScopeRef scope) =>
        $"api/datasourceprofiles/{Uri.EscapeDataString(name)}?namespace={Uri.EscapeDataString(ns.Value)}&scopeRef={Uri.EscapeDataString(scope.Value)}";
    private static string ChildPath(ResourceNamespace ns, string name, ResourceScopeRef scope, string child) =>
        $"api/datasourceprofiles/{Uri.EscapeDataString(name)}/{child}?namespace={Uri.EscapeDataString(ns.Value)}&scopeRef={Uri.EscapeDataString(scope.Value)}";
}
