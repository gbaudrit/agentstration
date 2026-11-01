using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Console;

public sealed record ResourceAccessPolicySnapshot(ResourceAccessPolicy Policy, string ETag);

public interface IResourceAccessPolicyClient
{
    Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken);
    Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag,
        CancellationToken cancellationToken);
}

public sealed class ResourceAccessPolicyApiClient(HttpClient httpClient) : IResourceAccessPolicyClient
{
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
