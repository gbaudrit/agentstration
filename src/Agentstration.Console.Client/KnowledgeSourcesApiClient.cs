using System.Net.Http.Json;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IKnowledgeSourcesClient
{
    Task<IReadOnlyList<KnowledgeSourceResource>> GetAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<KnowledgeSourceReadiness> GetReadinessAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(ResourceNamespace @namespace, string name,
        PublishKnowledgeSourceToolExposureRequest request, CancellationToken cancellationToken = default);
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
}
