using System.Net.Http.Json;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IKnowledgeSourceProfilesClient
{
    Task<IReadOnlyList<KnowledgeSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileResource>> CreateAsync(CreateKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileResource>> UpdateAsync(ResourceNamespace @namespace, string name, PutKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> PublishAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileResource>> ActivateAsync(ResourceNamespace @namespace, string name, ActivateKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default);
    Task<KnowledgeSourceProfileApplicationPlan> PreviewApplicationAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, CancellationToken cancellationToken = default);
    Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> ApplyAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, string etag, CancellationToken cancellationToken = default);
    Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default);
}

public sealed class KnowledgeSourceProfilesApiClient(HttpClient httpClient) : IKnowledgeSourceProfilesClient
{
    public Task<IReadOnlyList<KnowledgeSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeSourceProfileResource>>(httpClient, "api/knowledgesourceprofiles", cancellationToken);

    public async Task<ResourceSnapshot<KnowledgeSourceProfileResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        await GetResourceAsync<KnowledgeSourceProfileResource>(Path(@namespace, name), cancellationToken);

    public Task<IReadOnlyList<KnowledgeSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<KnowledgeSourceProfileRevisionResource>>(httpClient, ChildPath(@namespace, name, "revisions"), cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> CreateAsync(CreateKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default) =>
        SendResourceAsync<KnowledgeSourceProfileResource>(HttpMethod.Post, "api/knowledgesourceprofiles", request, null, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> UpdateAsync(ResourceNamespace @namespace, string name, PutKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendResourceAsync<KnowledgeSourceProfileResource>(HttpMethod.Put, Path(@namespace, name), request, etag, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> PublishAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default) =>
        SendResourceAsync<KnowledgeSourceProfileRevisionResource>(HttpMethod.Post, ChildPath(@namespace, name, "revisions"), request, null, cancellationToken);

    public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> ActivateAsync(ResourceNamespace @namespace, string name, ActivateKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendResourceAsync<KnowledgeSourceProfileResource>(HttpMethod.Put, ChildPath(@namespace, name, "active-revision"), request, etag, cancellationToken);

    public async Task<KnowledgeSourceProfileApplicationPlan> PreviewApplicationAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ChildPath(@namespace, name, "application-plan"), request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceProfileApplicationPlan>(cancellationToken))!;
    }

    public Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> ApplyAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, string etag, CancellationToken cancellationToken = default) =>
        SendResourceAsync<KnowledgeSourceProfileRevisionResource>(HttpMethod.Post, ChildPath(@namespace, name, "applications"), request, etag, cancellationToken);

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, name));
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

    private async Task<ResourceSnapshot<T>> SendResourceAsync<T>(HttpMethod method, string path, object body, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await SnapshotAsync<T>(response, cancellationToken);
    }

    private static async Task<ResourceSnapshot<T>> SnapshotAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        new((await response.Content.ReadFromJsonAsync<T>(cancellationToken))!,
            response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Missing ETag."));

    private static string Path(ResourceNamespace @namespace, string name)
    {
        var path = $"api/knowledgesourceprofiles/{Uri.EscapeDataString(name)}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }

    private static string ChildPath(ResourceNamespace @namespace, string name, string child)
    {
        var path = $"api/knowledgesourceprofiles/{Uri.EscapeDataString(name)}/{child}";
        return @namespace.IsDefault ? path : $"{path}?namespace={Uri.EscapeDataString(@namespace.Value)}";
    }
}
