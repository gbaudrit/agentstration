using System.Net.Http.Json;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IKnowledgeSourcesClient
{
    Task<IReadOnlyList<KnowledgeSourceResource>> GetAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceResource>> CreateAsync(CreateKnowledgeSourceRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceResource>> UpdateAsync(ResourceNamespace @namespace, string name,
        PutKnowledgeSourceRequest request, string etag, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceResource>> SetEnabledAsync(ResourceNamespace @namespace, string name,
        bool enabled, string etag, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default);
    Task<KnowledgeSourceReadiness> GetReadinessAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(ResourceNamespace @namespace, string name,
        PublishKnowledgeSourceToolExposureRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace @namespace, string name,
        StartKnowledgeAcquisitionRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeAcquisitionResource>> CancelAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeAcquisitionResource>> RetryAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId,
        RetryKnowledgeAcquisitionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeProjectionResource>> GetProjectionsAsync(ResourceNamespace @namespace, string name,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeProjectionResource>>([]);
    Task<ResourceSnapshot<KnowledgeProjectionResource>> StartProjectionAsync(ResourceNamespace @namespace, string name,
        StartKnowledgeProjectionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This client does not support Knowledge projections.");
    Task<IReadOnlyList<KnowledgeSnapshotView>> GetSnapshotsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<KnowledgeSnapshotView> SelectActiveSnapshotAsync(ResourceNamespace @namespace, string name, string snapshotName, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSnapshotResource>> PublishSnapshotAsync(ResourceNamespace @namespace, string acquisitionId,
        PublishKnowledgeSnapshotRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task<KnowledgeRetrievalResult> SearchAsync(ResourceNamespace @namespace, string name, SearchKnowledgeRequest request, CancellationToken cancellationToken = default);
    Task<KnowledgeRetrievalResult> QueryAsync(ResourceNamespace @namespace, string name, QueryKnowledgeRequest request, CancellationToken cancellationToken = default);
    Task<KnowledgeRetrievalResult> ReadAsync(ResourceNamespace @namespace, string name, ReadKnowledgeRequest request, CancellationToken cancellationToken = default);
}

public sealed class KnowledgeSourcesApiClient(HttpClient httpClient) : IKnowledgeSourcesClient
{
    public Task<IReadOnlyList<KnowledgeSourceResource>> GetAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeSourceResource>>(httpClient, "api/knowledgesources", cancellationToken);

    public async Task<ResourceSnapshot<KnowledgeSourceResource>> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(Path(@namespace, name), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new(
            (await response.Content.ReadFromJsonAsync<KnowledgeSourceResource>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    public Task<ResourceSnapshot<KnowledgeSourceResource>> CreateAsync(
        CreateKnowledgeSourceRequest request,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeSourceResource>(
            HttpMethod.Post, "api/knowledgesources", request, null, null, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceResource>> UpdateAsync(
        ResourceNamespace @namespace,
        string name,
        PutKnowledgeSourceRequest request,
        string etag,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeSourceResource>(
            HttpMethod.Put, Path(@namespace, name), request, etag, null, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceResource>> SetEnabledAsync(
        ResourceNamespace @namespace,
        string name,
        bool enabled,
        string etag,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeSourceResource>(
            HttpMethod.Put, ChildPath(@namespace, name, "enabled"), new SetKnowledgeSourceEnabledRequest(enabled), etag, null, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, name));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<KnowledgeSourceReadiness> GetReadinessAsync(ResourceNamespace @namespace, string name,
        CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<KnowledgeSourceReadiness>(httpClient, ChildPath(@namespace, name, "readiness"), cancellationToken);

    public async Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(ChildPath(@namespace, name, "tool-exposure"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<KnowledgeSourceToolExposureResource>(cancellationToken);
    }

    public async Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(
        ResourceNamespace @namespace,
        string name,
        PublishKnowledgeSourceToolExposureRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ChildPath(@namespace, name, "tool-exposure"), request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new(
            (await response.Content.ReadFromJsonAsync<KnowledgeSourceToolExposureResource>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    public Task<IReadOnlyList<KnowledgeAcquisitionResource>> GetAcquisitionsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeAcquisitionResource>>(httpClient,
            ChildPath(@namespace, name, "acquisitions"), cancellationToken);

    public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> StartAcquisitionAsync(
        ResourceNamespace @namespace,
        string name,
        StartKnowledgeAcquisitionRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeAcquisitionResource>(
            HttpMethod.Post, ChildPath(@namespace, name, "acquisitions"), request, null, idempotencyKey, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> CancelAcquisitionAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeAcquisitionResource>(
            HttpMethod.Post, AcquisitionPath(@namespace, acquisitionId, "cancel"), null, null, null, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> RetryAcquisitionAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        RetryKnowledgeAcquisitionRequest request,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeAcquisitionResource>(
            HttpMethod.Post, AcquisitionPath(@namespace, acquisitionId, "retry"), request, null, null, cancellationToken);

    public Task<IReadOnlyList<KnowledgeProjectionResource>> GetProjectionsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeProjectionResource>>(httpClient,
            ChildPath(@namespace, name, "projections"), cancellationToken);

    public Task<ResourceSnapshot<KnowledgeProjectionResource>> StartProjectionAsync(
        ResourceNamespace @namespace,
        string name,
        StartKnowledgeProjectionRequest request,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeProjectionResource>(
            HttpMethod.Post, ChildPath(@namespace, name, "projections"), request, null, null, cancellationToken);

    public Task<IReadOnlyList<KnowledgeSnapshotView>> GetSnapshotsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeSnapshotView>>(httpClient,
            ChildPath(@namespace, name, "snapshots"), cancellationToken);

    public async Task<KnowledgeSnapshotView> SelectActiveSnapshotAsync(
        ResourceNamespace @namespace,
        string name,
        string snapshotName,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ChildPath(@namespace, name, "snapshots/active"),
            new SelectActiveKnowledgeSnapshotRequest(snapshotName), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<KnowledgeSnapshotView>(cancellationToken))!;
    }

    public Task<ResourceSnapshot<KnowledgeSnapshotResource>> PublishSnapshotAsync(
        ResourceNamespace @namespace,
        string acquisitionId,
        PublishKnowledgeSnapshotRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) => SendResourceAsync<KnowledgeSnapshotResource>(
            HttpMethod.Post, AcquisitionPath(@namespace, acquisitionId, "snapshots"), request, null, idempotencyKey, cancellationToken);

    public Task<KnowledgeRetrievalResult> SearchAsync(ResourceNamespace @namespace, string name,
        SearchKnowledgeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<KnowledgeRetrievalResult>(ChildPath(@namespace, name, "search"), request, cancellationToken);

    public Task<KnowledgeRetrievalResult> QueryAsync(ResourceNamespace @namespace, string name,
        QueryKnowledgeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<KnowledgeRetrievalResult>(ChildPath(@namespace, name, "query"), request, cancellationToken);

    public Task<KnowledgeRetrievalResult> ReadAsync(ResourceNamespace @namespace, string name,
        ReadKnowledgeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<KnowledgeRetrievalResult>(ChildPath(@namespace, name, "read"), request, cancellationToken);

    private async Task<ResourceSnapshot<T>> SendResourceAsync<T>(HttpMethod method, string path, object? body,
        string? etag, string? idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return new((await response.Content.ReadFromJsonAsync<T>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, body, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
    }

    private static string Path(ResourceNamespace @namespace, string name)
    {
        var path = $"api/knowledgesources/{Uri.EscapeDataString(name)}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }

    private static string ChildPath(ResourceNamespace @namespace, string name, string child)
    {
        var path = $"api/knowledgesources/{Uri.EscapeDataString(name)}/{child}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }

    private static string AcquisitionPath(ResourceNamespace @namespace, string id, string child)
    {
        var path = $"api/knowledgeacquisitions/{Uri.EscapeDataString(id)}/{child}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }
}
