using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IModelProvidersClient
{
    Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken);
    Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken);
    Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken);
    Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken);
    Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken);
}

public interface IModelProfilesClient
{
    Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken);
    Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken);
    Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken);
}

public interface IAgentsModelClient
{
    Task<AgentModelResponse> GetAgentModelResolutionAsync(string agentName, CancellationToken cancellationToken);
}

public interface IRuntimeProfilesClient
{
    Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken);
    Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken);
}

public sealed class ModelProvidersApiClient(HttpClient httpClient) : IModelProvidersClient
{
    public async Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ModelProviderResponse>>(httpClient, "api/modelproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Get, Path(providerName), null, null, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Post, "api/modelproviders", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Put, Path(providerName), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(providerName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProviderUsagesResponse>(httpClient, ChildPath(providerName, "usages"), cancellationToken);

    public async Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<AvailableModelResponse>>(httpClient, ChildPath(providerName, "models"), cancellationToken)).Value;

    public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProviderStatusResponse>(httpClient, ChildPath(providerName, "status"), cancellationToken);

    public async Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ChildPath(providerName, "test"), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModelProviderStatusResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty provider status.", Guid.NewGuid().ToString("N"));
    }

    private async Task<ResourceSnapshot<ModelProviderResource>> ReadResourceAsync(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<ModelProviderResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty model provider.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag)) throw new AgentstrationApiException("Agentstration API did not return the provider ETag.", Guid.NewGuid().ToString("N"));
        return new ResourceSnapshot<ModelProviderResource>(value, responseEtag);
    }

    private static string Path(string providerName) => $"api/modelproviders/{Escape(providerName)}";
    private static string ChildPath(string providerName, string child) => $"api/modelproviders/{Escape(providerName)}/{child}";

    private static string Escape(string value) => Uri.EscapeDataString(value);
}

public sealed class ModelProfilesApiClient(HttpClient httpClient) : IModelProfilesClient
{
    public async Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        AddQuery(query, "search", search);
        AddQuery(query, "provider", provider);
        AddQuery(query, "status", status);
        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        return (await ApiResponse.ReadAsync<ValueResponse<ModelProfileSummaryResponse>>(httpClient, "api/modelprofiles" + suffix, cancellationToken)).Value;
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ProfilePath(profileName), cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/modelprofiles", request, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ProfilePath(profileName)) { Content = JsonContent.Create(request) };
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, ProfilePath(profileName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProfileUsagesResponse>(httpClient, ProfilePath(profileName, "usages"), cancellationToken);

    public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProfileResolutionResponse>(httpClient, ProfilePath(profileName, "resolution"), cancellationToken);

    private static string ProfilePath(string profileName, string? child = null)
    {
        var path = $"api/modelprofiles/{Uri.EscapeDataString(profileName)}";
        return child is null ? path : $"{path}/{child}";
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) query.Add($"{name}={Uri.EscapeDataString(value)}");
    }

    private static async Task<ResourceSnapshot<ModelProfileResource>> ReadResourceAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<ModelProfileResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty model profile.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the model profile ETag.", Guid.NewGuid().ToString("N"));
        return new ResourceSnapshot<ModelProfileResource>(value, etag);
    }
}

public sealed class AgentsModelApiClient(HttpClient httpClient) : IAgentsModelClient
{
    public Task<AgentModelResponse> GetAgentModelResolutionAsync(string agentName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<AgentModelResponse>(httpClient, $"api/agents/{Uri.EscapeDataString(agentName)}/model", cancellationToken);
}

public sealed class RuntimeProfilesApiClient(HttpClient httpClient) : IRuntimeProfilesClient
{
    public async Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<RuntimeProfileSummaryResponse>>(httpClient, "api/runtimeprofiles", cancellationToken)).Value;

    public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Get, Path(profileName), null, null, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Post, "api/runtimeprofiles", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Put, Path(profileName), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(profileName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<RuntimeProfileUsagesResponse>(httpClient, $"api/runtimeprofiles/{Uri.EscapeDataString(profileName)}/usages", cancellationToken);

    private async Task<ResourceSnapshot<RuntimeProfileResource>> ReadAsync(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<RuntimeProfileResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty runtime profile.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag)) throw new AgentstrationApiException("Agentstration API did not return the runtime profile ETag.", Guid.NewGuid().ToString("N"));
        return new ResourceSnapshot<RuntimeProfileResource>(value, responseEtag);
    }

    private static string Path(string profileName) => $"api/runtimeprofiles/{Uri.EscapeDataString(profileName)}";
}
