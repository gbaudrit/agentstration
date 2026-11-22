using System.Net;
using System.Net.Http.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IBootstrapProfilesApiClient
{
    Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AppliedBootstrapProfile>> ListAppliedProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AppliedBootstrapProfile>>([]);
    Task<IReadOnlyList<AppliedBootstrapVersion>> ListVersionsAsync(string profileId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<IReadOnlyList<ScopedBootstrapApplication>> ListHistoryAsync(string profileId, string version, ResourceScopeRef scope,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ScopedBootstrapApplication?> GetScopedApplicationAsync(Guid id, ResourceScopeRef scope, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken);
    Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken);
    Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken, Guid? requestId = null);
    Task<BootstrapApplicationResource> CancelAsync(string applicationId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken);
}

public sealed class BootstrapProfilesApiClient(HttpClient httpClient) : IBootstrapProfilesApiClient
{
    public Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapManagementView>(httpClient, "api/bootstrap/profiles", cancellationToken);

    public async Task<IReadOnlyList<AppliedBootstrapProfile>> ListAppliedProfilesAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<AppliedBootstrapProfile[]>(httpClient, "api/bootstrap/applied-profiles", cancellationToken);

    public async Task<IReadOnlyList<AppliedBootstrapVersion>> ListVersionsAsync(string profileId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<AppliedBootstrapVersion[]>(httpClient,
            $"api/bootstrap/applied-profiles/{Uri.EscapeDataString(profileId)}/versions", cancellationToken);

    public async Task<IReadOnlyList<ScopedBootstrapApplication>> ListHistoryAsync(string profileId, string version,
        ResourceScopeRef scope, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<ScopedBootstrapApplication[]>(httpClient,
            $"api/bootstrap/applied-profiles/{Uri.EscapeDataString(profileId)}/versions/{Uri.EscapeDataString(version)}/applications?scope={Uri.EscapeDataString(scope.Value)}",
            cancellationToken);

    public async Task<ScopedBootstrapApplication?> GetScopedApplicationAsync(Guid id, ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/bootstrap/scoped-applications/{id:D}?scope={Uri.EscapeDataString(scope.Value)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<ScopedBootstrapApplication>(response, "scoped Bootstrap application", cancellationToken);
    }

    public async Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/bootstrap/source-profile", selection, cancellationToken);
        return await ReadAsync<BootstrapProfileSummary>(response, "source Bootstrap Profile", cancellationToken);
    }

    public async Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/bootstrap/binding-targets", request, cancellationToken);
        return await ReadAsync<BootstrapBindingTargetOption[]>(response, "Bootstrap binding targets", cancellationToken);
    }

    public async Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/bootstrap/profiles/preview",
            new BootstrapProfilePreviewRequest(selection.Profiles, selection.Target, selection.Bindings, selection.Source, selection.Applications),
            cancellationToken);
        return await ReadAsync<BootstrapCompositionPreview>(response, "Bootstrap preview", cancellationToken);
    }

    public async Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken, Guid? requestId = null)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/bootstrap/applications",
            new ApplyBootstrapProfilesRequest(selection.Profiles, expectedDigest, selection.Target, selection.Bindings, selection.Source, requestId, selection.Applications),
            cancellationToken);
        return await ReadAsync<BootstrapApplicationResource>(response, "Bootstrap application", cancellationToken);
    }

    public async Task<BootstrapApplicationResource> CancelAsync(string applicationId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/bootstrap/applications/{Uri.EscapeDataString(applicationId)}/cancel", null, cancellationToken);
        return await ReadAsync<BootstrapApplicationResource>(response, "Bootstrap application", cancellationToken);
    }

    public async Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/bootstrap/applications/{Uri.EscapeDataString(applicationId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<BootstrapApplicationResource>(response, "Bootstrap application", cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string description, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException($"Agentstration API returned an empty {description}.", Guid.NewGuid().ToString("N"));
    }
}
