using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IModelProvidersClient
{
    Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) => GetModelProviderAsync(providerName, cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(ResourceNamespace @namespace, string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) => UpdateModelProviderAsync(providerName, request, etag, cancellationToken);
    Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken);
    Task DeleteModelProviderAsync(ResourceNamespace @namespace, string providerName, string etag, CancellationToken cancellationToken) => DeleteModelProviderAsync(providerName, etag, cancellationToken);
    Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken);
    Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) => GetModelProviderUsagesAsync(providerName, cancellationToken);
    Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken);
    Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) => GetProviderModelsAsync(providerName, cancellationToken);
    Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken);
    Task<ModelProviderStatusResponse> GetProviderStatusAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) => GetProviderStatusAsync(providerName, cancellationToken);
    Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken);
    Task<ModelProviderStatusResponse> TestProviderAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) => TestProviderAsync(providerName, cancellationToken);
}

public interface IExtensionsClient
{
    Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken);
    Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken);
    Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken);
}

public sealed class ExtensionsApiClient(HttpClient httpClient) : IExtensionsClient
{
    public async Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ExtensionResponse>>(httpClient, "api/extensions", cancellationToken)).Value;

    public async Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync("api/extensions/discover", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ExtensionDiscoveryResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty extension discovery result.", Guid.NewGuid().ToString("N"));
    }

    public async Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ExtensionRegistrationResource>>(httpClient, "api/extensionregistrations", cancellationToken)).Value;

    public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ReadRegistrationAsync(HttpMethod.Get, RegistrationPath(@namespace, name), null, null, cancellationToken);

    public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) =>
        ReadRegistrationAsync(HttpMethod.Post, "api/extensionregistrations", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) =>
        ReadRegistrationAsync(HttpMethod.Put, RegistrationPath(@namespace, name), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, RegistrationPath(@namespace, name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<ResourceSnapshot<ExtensionRegistrationResource>> ReadRegistrationAsync(HttpMethod method, string path, HttpContent? content, string? etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<ExtensionRegistrationResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty extension registration.", Guid.NewGuid().ToString("N"));
        var responseEtag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(responseEtag)) throw new AgentstrationApiException("Agentstration API did not return the extension registration ETag.", Guid.NewGuid().ToString("N"));
        return new(value, responseEtag);
    }

    private static string RegistrationPath(ResourceNamespace @namespace, string name) =>
        $"api/extensionregistrations/{Uri.EscapeDataString(name)}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
}

public interface IModelProfilesClient
{
    Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) => GetModelProfileAsync(profileName, cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(ResourceNamespace @namespace, string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken) => UpdateModelProfileAsync(profileName, request, etag, cancellationToken);
    Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken);
    Task DeleteModelProfileAsync(ResourceNamespace @namespace, string profileName, string etag, CancellationToken cancellationToken) => DeleteModelProfileAsync(profileName, etag, cancellationToken);
    Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken);
    Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) => GetModelProfileUsagesAsync(profileName, cancellationToken);
    Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken);
    Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) => GetModelProfileResolutionAsync(profileName, cancellationToken);
    Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, CancellationToken cancellationToken);
    Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, string etag, CancellationToken cancellationToken);
}

public interface IAgentsModelClient
{
    Task<AgentModelResponse> GetAgentModelResolutionAsync(string agentName, CancellationToken cancellationToken);
    Task<AgentModelResponse> GetAgentModelResolutionAsync(ResourceNamespace @namespace, string agentName, CancellationToken cancellationToken) =>
        @namespace.IsDefault ? GetAgentModelResolutionAsync(agentName, cancellationToken) : throw new NotSupportedException("This client does not support namespaced Agents.");
}

public interface IRuntimeProfilesClient
{
    Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) => GetRuntimeProfileAsync(profileName, cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken);
    Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) => UpdateRuntimeProfileAsync(profileName, request, etag, cancellationToken);
    Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken);
    Task DeleteRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, string etag, CancellationToken cancellationToken) => DeleteRuntimeProfileAsync(profileName, etag, cancellationToken);
    Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken);
    Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) => GetRuntimeProfileUsagesAsync(profileName, cancellationToken);
}

public sealed class ModelProvidersApiClient(HttpClient httpClient) : IModelProvidersClient
{
    public async Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<ModelProviderResponse>>(httpClient, "api/modelproviders", cancellationToken)).Value;

    public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) =>
        GetModelProviderAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Get, Path(@namespace, providerName), null, null, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Post, "api/modelproviders", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) =>
        UpdateModelProviderAsync(ResourceNamespace.Default, providerName, request, etag, cancellationToken);

    public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(ResourceNamespace @namespace, string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) =>
        ReadResourceAsync(HttpMethod.Put, Path(@namespace, providerName), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken)
        => await DeleteModelProviderAsync(ResourceNamespace.Default, providerName, etag, cancellationToken);

    public async Task DeleteModelProviderAsync(ResourceNamespace @namespace, string providerName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, providerName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) =>
        GetModelProviderUsagesAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProviderUsagesResponse>(httpClient, ChildPath(@namespace, providerName, "usages"), cancellationToken);

    public async Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) =>
        await GetProviderModelsAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public async Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<AvailableModelResponse>>(httpClient, ChildPath(@namespace, providerName, "models"), cancellationToken)).Value;

    public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) =>
        GetProviderStatusAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public Task<ModelProviderStatusResponse> GetProviderStatusAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProviderStatusResponse>(httpClient, ChildPath(@namespace, providerName, "status"), cancellationToken);

    public async Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken)
        => await TestProviderAsync(ResourceNamespace.Default, providerName, cancellationToken);

    public async Task<ModelProviderStatusResponse> TestProviderAsync(ResourceNamespace @namespace, string providerName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(ChildPath(@namespace, providerName, "test"), null, cancellationToken);
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

    private static string Path(ResourceNamespace @namespace, string providerName) => $"api/modelproviders/{Escape(providerName)}?resourceNamespace={Escape(@namespace.Value)}";
    private static string ChildPath(ResourceNamespace @namespace, string providerName, string child) => $"api/modelproviders/{Escape(providerName)}/{child}?resourceNamespace={Escape(@namespace.Value)}";

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
        => await GetModelProfileAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public async Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ProfilePath(@namespace, profileName), cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/modelprofiles", request, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken)
        => await UpdateModelProfileAsync(ResourceNamespace.Default, profileName, request, etag, cancellationToken);

    public async Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(ResourceNamespace @namespace, string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, ProfilePath(@namespace, profileName)) { Content = JsonContent.Create(request) };
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken)
        => await DeleteModelProfileAsync(ResourceNamespace.Default, profileName, etag, cancellationToken);

    public async Task DeleteModelProfileAsync(ResourceNamespace @namespace, string profileName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, ProfilePath(@namespace, profileName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        GetModelProfileUsagesAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProfileUsagesResponse>(httpClient, ProfilePath(@namespace, profileName, "usages"), cancellationToken);

    public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) =>
        GetModelProfileResolutionAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ModelProfileResolutionResponse>(httpClient, ProfilePath(@namespace, profileName, "resolution"), cancellationToken);

    public async Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(
        ResourceNamespace @namespace,
        string profileName,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ProfilePath(@namespace, profileName, "option-migrations/preview"),
            new PreviewModelProfileOptionMigrationRequest(targetVersion),
            cancellationToken);
        return await ReadSnapshotAsync<ModelProfileOptionMigrationPreviewResponse>(response, "option migration preview", cancellationToken);
    }

    public async Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(
        ResourceNamespace @namespace,
        string profileName,
        string targetVersion,
        string etag,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ProfilePath(@namespace, profileName, "option-migrations/apply"))
        {
            Content = JsonContent.Create(new PreviewModelProfileOptionMigrationRequest(targetVersion))
        };
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    private static string ProfilePath(ResourceNamespace @namespace, string profileName, string? child = null)
    {
        var path = $"api/modelprofiles/{Uri.EscapeDataString(profileName)}";
        if (child is not null) path += $"/{child}";
        return $"{path}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
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

    private static async Task<ResourceSnapshot<T>> ReadSnapshotAsync<T>(HttpResponseMessage response, string resourceName, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException($"Agentstration API returned an empty {resourceName}.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException($"Agentstration API did not return the {resourceName} ETag.", Guid.NewGuid().ToString("N"));
        return new(value, etag);
    }
}

public sealed class AgentsModelApiClient(HttpClient httpClient) : IAgentsModelClient
{
    public Task<AgentModelResponse> GetAgentModelResolutionAsync(string agentName, CancellationToken cancellationToken) =>
        GetAgentModelResolutionAsync(ResourceNamespace.Default, agentName, cancellationToken);

    public Task<AgentModelResponse> GetAgentModelResolutionAsync(ResourceNamespace @namespace, string agentName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<AgentModelResponse>(httpClient,
            @namespace.IsDefault
                ? $"api/agents/{Uri.EscapeDataString(agentName)}/model"
                : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/agents/{Uri.EscapeDataString(agentName)}/model",
            cancellationToken);
}

public sealed class RuntimeProfilesApiClient(HttpClient httpClient) : IRuntimeProfilesClient
{
    public async Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<ValueResponse<RuntimeProfileSummaryResponse>>(httpClient, "api/runtimeprofiles", cancellationToken)).Value;

    public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken) =>
        GetRuntimeProfileAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Get, Path(@namespace, profileName), null, null, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Post, "api/runtimeprofiles", JsonContent.Create(request), null, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) =>
        UpdateRuntimeProfileAsync(ResourceNamespace.Default, profileName, request, etag, cancellationToken);

    public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) =>
        ReadAsync(HttpMethod.Put, Path(@namespace, profileName), JsonContent.Create(request), etag, cancellationToken);

    public async Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken)
        => await DeleteRuntimeProfileAsync(ResourceNamespace.Default, profileName, etag, cancellationToken);

    public async Task DeleteRuntimeProfileAsync(ResourceNamespace @namespace, string profileName, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, profileName));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
        GetRuntimeProfileUsagesAsync(ResourceNamespace.Default, profileName, cancellationToken);

    public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(ResourceNamespace @namespace, string profileName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<RuntimeProfileUsagesResponse>(httpClient, $"api/runtimeprofiles/{Uri.EscapeDataString(profileName)}/usages?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}", cancellationToken);

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

    private static string Path(ResourceNamespace @namespace, string profileName) => $"api/runtimeprofiles/{Uri.EscapeDataString(profileName)}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
}
