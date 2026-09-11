using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface ISourceRegistriesClient
{
    Task<IReadOnlyList<SourceRegistryRegistrationView>> GetRegistriesAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceRegistryRegistrationView>> GetRegistryAsync(string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceRegistryRegistrationView>> CreateRegistryAsync(CreateSourceRegistryRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceRegistryRegistrationView>> UpdateRegistryAsync(string name, PutSourceRegistryRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteRegistryAsync(string name, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<SourceRegistryRegistrationView>> RefreshRegistryAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceRegistryRefreshRecordResource>> GetRefreshesAsync(string name, CancellationToken cancellationToken);
    Task<SourceRegistryOriginTrustView> GetOriginTrustAsync(string name, CancellationToken cancellationToken);
    Task<SourceRegistryDiscoveryPage> SearchAsync(SourceRegistryDiscoveryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceRegistryDiscoveryPublisher>> GetPublishersAsync(CancellationToken cancellationToken);
    Task<SourceRegistryDiscoverySource?> GetSourceAsync(string publisher, string sourceName, CancellationToken cancellationToken);
    Task<SourceImportResult> ImportAsync(SourceRegistryObservationSelection selection, ResourceScopeRef? scopeRef, CancellationToken cancellationToken);
}

public sealed class SourceRegistriesApiClient(HttpClient httpClient) : ISourceRegistriesClient
{
    public async Task<IReadOnlyList<SourceRegistryRegistrationView>> GetRegistriesAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<SourceRegistryRegistrationView>>(httpClient, "api/sourceregistries", cancellationToken)).Value;

    public Task<ResourceSnapshot<SourceRegistryRegistrationView>> GetRegistryAsync(string name, CancellationToken cancellationToken) =>
        ReadSnapshotAsync(HttpMethod.Get, RegistryPath(name), null, null, cancellationToken);

    public Task<ResourceSnapshot<SourceRegistryRegistrationView>> CreateRegistryAsync(CreateSourceRegistryRequest request, CancellationToken cancellationToken) =>
        ReadSnapshotAsync(HttpMethod.Post, "api/sourceregistries", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<SourceRegistryRegistrationView>> UpdateRegistryAsync(string name, PutSourceRegistryRequest request, string etag, CancellationToken cancellationToken) =>
        ReadSnapshotAsync(HttpMethod.Put, RegistryPath(name), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteRegistryAsync(string name, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, RegistryPath(name));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<ResourceSnapshot<SourceRegistryRegistrationView>> RefreshRegistryAsync(string name, CancellationToken cancellationToken) =>
        ReadSnapshotAsync(HttpMethod.Post, $"{RegistryPath(name)}/refresh", null, null, cancellationToken);

    public async Task<IReadOnlyList<SourceRegistryRefreshRecordResource>> GetRefreshesAsync(string name, CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<SourceRegistryRefreshHistoryResponse>(httpClient, $"{RegistryPath(name)}/refreshes?take=50", cancellationToken)).Value;

    public Task<SourceRegistryOriginTrustView> GetOriginTrustAsync(string name, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<SourceRegistryOriginTrustView>(httpClient, $"{RegistryPath(name)}/trust", cancellationToken);

    public Task<SourceRegistryDiscoveryPage> SearchAsync(SourceRegistryDiscoveryQuery query, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<SourceRegistryDiscoveryPage>(httpClient, DiscoveryPath(query), cancellationToken);

    public async Task<IReadOnlyList<SourceRegistryDiscoveryPublisher>> GetPublishersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<SourceRegistryDiscoveryPublisher>>(httpClient, "api/sourceregistries/discovery/publishers", cancellationToken)).Value;

    public async Task<SourceRegistryDiscoverySource?> GetSourceAsync(string publisher, string sourceName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/sourceregistries/discovery/sources/{Uri.EscapeDataString(publisher)}/{Uri.EscapeDataString(sourceName)}",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SourceRegistryDiscoverySource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty discovered Source.", Guid.NewGuid().ToString("N"));
    }

    public async Task<SourceImportResult> ImportAsync(SourceRegistryObservationSelection selection, ResourceScopeRef? scopeRef, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/sourceregistries/discovery/imports",
            new ImportSourceRegistryObservationRequest(selection, scopeRef), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SourceImportResult>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Source import result.", Guid.NewGuid().ToString("N"));
    }

    private async Task<ResourceSnapshot<SourceRegistryRegistrationView>> ReadSnapshotAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string? etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<SourceRegistryRegistrationView>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Source registry.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag))
            throw new AgentstrationApiException("Agentstration API did not return the Source registry ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string RegistryPath(string name) => $"api/sourceregistries/{Uri.EscapeDataString(name)}";

    private static string DiscoveryPath(SourceRegistryDiscoveryQuery query)
    {
        var values = new List<string>();
        Add(values, "search", query.Search);
        Add(values, "publisher", query.Publisher);
        Add(values, "registry", query.Registry);
        values.Add($"compatibleOnly={query.CompatibleOnly.ToString().ToLowerInvariant()}");
        values.Add($"freshOnly={query.FreshOnly.ToString().ToLowerInvariant()}");
        values.Add($"conflictsOnly={query.ConflictsOnly.ToString().ToLowerInvariant()}");
        Add(values, "trustPolicy", query.TrustPolicy?.ToString());
        Add(values, "publisherStatus", query.PublisherStatus?.ToString());
        Add(values, "verificationStatus", query.VerificationStatus?.ToString());
        values.Add($"skip={query.Skip}");
        values.Add($"take={query.Take}");
        return $"api/sourceregistries/discovery?{string.Join('&', values)}";
    }

    private static void Add(ICollection<string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add($"{name}={Uri.EscapeDataString(value)}");
    }
}
