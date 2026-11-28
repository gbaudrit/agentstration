using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Console;

public sealed record ResourceAccessPolicySnapshot(ResourceAccessPolicy Policy, string ETag);

public interface IResourceAccessPolicyClient
{
    Task<ResourceRelatedAccessSnapshot> GetRelatedAsync(Guid uid, CancellationToken token) => throw new NotSupportedException();
    Task<ResourceRelatedAccessEvaluation> EvaluateRelatedAsync(Guid uid, Agentstration.Resources.ResourceScopeRef consumer, CancellationToken token) => throw new NotSupportedException();
    Task<IReadOnlyList<ResourceRelatedAccessResult>> ApplyRelatedAsync(Guid uid, ResourceRelatedAccessRequest request, CancellationToken token) => throw new NotSupportedException();
    Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request, CancellationToken cancellationToken);
    Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken);
    Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag,
        CancellationToken cancellationToken);
}

public sealed class ResourceAccessPolicyApiClient(HttpClient httpClient) : IResourceAccessPolicyClient
{
    public async Task<ResourceRelatedAccessSnapshot> GetRelatedAsync(Guid uid, CancellationToken token)
    {
        using var response = await httpClient.GetAsync($"{Path(uid)}/related", token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<ResourceRelatedAccessSnapshot>(token)
            ?? throw new InvalidOperationException("The related access response is empty.");
    }
    public async Task<ResourceRelatedAccessEvaluation> EvaluateRelatedAsync(Guid uid, Agentstration.Resources.ResourceScopeRef consumer, CancellationToken token)
    {
        using var response = await httpClient.GetAsync($"{Path(uid)}/related/evaluate?consumerScopeRef={Uri.EscapeDataString(consumer.Value)}", token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<ResourceRelatedAccessEvaluation>(token)
            ?? throw new InvalidOperationException("The related access evaluation is empty.");
    }
    public async Task<IReadOnlyList<ResourceRelatedAccessResult>> ApplyRelatedAsync(Guid uid, ResourceRelatedAccessRequest body, CancellationToken token)
    {
        using var response = await httpClient.PostAsJsonAsync($"{Path(uid)}/related", body, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<ResourceRelatedAccessResult[]>(token)
            ?? throw new InvalidOperationException("The related access result is empty.");
    }

    public async Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{Path(request.Resource.Uid)}/evaluate", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ResourceAccessDecision>(cancellationToken)
            ?? throw new AgentstrationApiException("The access decision response is empty.", Guid.NewGuid().ToString("N"));
    }
    public async Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(Path(resourceUid), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await ReadSnapshotAsync(response, cancellationToken);
    }

    public async Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path(policy.Resource.Uid))
        {
            Content = JsonContent.Create(policy)
        };
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await ReadSnapshotAsync(response, cancellationToken);
    }

    private static async Task<ResourceAccessPolicySnapshot> ReadSnapshotAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var policy = await response.Content.ReadFromJsonAsync<ResourceAccessPolicy>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty resource access policy.",
                Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the access policy ETag.",
                Guid.NewGuid().ToString("N"));
        return new(policy, etag);
    }

    private static string Path(Guid resourceUid) => $"api/resource-access-policies/{resourceUid:D}";
}
