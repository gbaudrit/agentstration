using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IBootstrapProfilesApiClient
{
    Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken);
    Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken);
    Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken);
    Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken);
}

public sealed class BootstrapProfilesApiClient(HttpClient httpClient) : IBootstrapProfilesApiClient
{
    public Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapManagementView>(httpClient, "api/bootstrap/profiles", cancellationToken);

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
            new BootstrapProfilePreviewRequest(selection.Profiles, selection.Target, selection.Bindings, selection.Source),
            cancellationToken);
        return await ReadAsync<BootstrapCompositionPreview>(response, "Bootstrap preview", cancellationToken);
    }

    public async Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/bootstrap/applications",
            new ApplyBootstrapProfilesRequest(selection.Profiles, expectedDigest, selection.Target, selection.Bindings, selection.Source),
            cancellationToken);
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
