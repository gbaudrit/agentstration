using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class EntryAdministrationApiClient(HttpClient httpClient, IHttpClientFactory httpClientFactory) : IEntryAdministrationApiClient
{
    public const string AgentResourceCatalogClient = "EntryAdministration.AgentResources";
    public const string FlowResourceCatalogClient = "EntryAdministration.FlowResources";

    public async Task<IReadOnlyList<EntryDraftResponse>> GetEntriesAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<EntryDraftResponse[]>(httpClient, "api/management/entries", cancellationToken);

    public Task<EntryDraftResponse> GetEntryAsync(string name, CancellationToken cancellationToken) =>
        GetEntryAsync(ResourceNamespace.Default, name, cancellationToken);

    public Task<EntryDraftResponse> GetEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<EntryDraftResponse>(httpClient, EntryPath(@namespace, name), cancellationToken);

    public Task<EntryDraft> SaveEntryAsync(EntryDraft draft, CancellationToken cancellationToken) => SaveEntryAsync(draft.Id.Namespace, draft, cancellationToken);

    public async Task<EntryDraft> SaveEntryAsync(ResourceNamespace @namespace, EntryDraft draft, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(EntryPath(@namespace, draft.Name), draft, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EntryDraft>(cancellationToken) ?? throw new AgentstrationApiException("Work API returned an empty Entry draft.", Guid.NewGuid().ToString("N"));
    }

    public Task<EntryValidationResponse> ValidateEntryAsync(string name, CancellationToken cancellationToken) => ValidateEntryAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<EntryValidationResponse> ValidateEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{EntryPath(@namespace, name)}/validate", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EntryValidationResponse>(cancellationToken) ?? throw new AgentstrationApiException("Work API returned an empty Entry validation.", Guid.NewGuid().ToString("N"));
    }

    public Task<EntryResource> PublishEntryAsync(string name, CancellationToken cancellationToken) => PublishEntryAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<EntryResource> PublishEntryAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{EntryPath(@namespace, name)}/publish", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EntryResource>(cancellationToken) ?? throw new AgentstrationApiException("Work API returned an empty published Entry.", Guid.NewGuid().ToString("N"));
    }

    public async Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(string name, CancellationToken cancellationToken) =>
        await GetDependenciesAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<EntryDependencyResponse[]>(httpClient, $"{EntryPath(@namespace, name)}/dependencies", cancellationToken);

    private static string EntryPath(ResourceNamespace @namespace, string name) => @namespace.IsDefault
        ? $"api/management/entries/{Uri.EscapeDataString(name)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/management/entries/{Uri.EscapeDataString(name)}";

    public async Task<IReadOnlyList<ResourcePickerItem>> GetResourcesAsync(EntryBindingKind kind, CancellationToken cancellationToken)
    {
        var resourceKind = kind == EntryBindingKind.Agent ? ResourceKinds.Agent : ResourceKinds.Flow;
        var catalogClient = httpClientFactory.CreateClient(kind == EntryBindingKind.Agent ? AgentResourceCatalogClient : FlowResourceCatalogClient);
        return await ApiResponse.ReadAsync<ResourcePickerItem[]>(catalogClient, $"api/resources?kind={Uri.EscapeDataString(resourceKind)}", cancellationToken);
    }

    public async Task<IReadOnlyList<EntryResponse>> GetPublishedEntriesAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<EntryResponse[]>(httpClient, "api/entries", cancellationToken);
    public async Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkplaceWorkspaceResponse[]>(httpClient, "api/workplace/workspaces", cancellationToken);
    public async Task<IReadOnlyList<WorkplaceDashboardDraftResponse>> GetDashboardsAsync(string workspaceName, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkplaceDashboardDraftResponse[]>(httpClient, $"api/management/workspaces/{Uri.EscapeDataString(workspaceName)}/dashboards", cancellationToken);
    public Task<WorkplaceDashboardDraftResponse> GetDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<WorkplaceDashboardDraftResponse>(httpClient, $"api/management/workspaces/{Uri.EscapeDataString(workspaceName)}/dashboards/{Uri.EscapeDataString(dashboardName)}", cancellationToken);
    public async Task<WorkplaceDashboardDraft> SaveDashboardAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/management/workspaces/{draft.WorkspaceId.Value:D}/dashboards/{Uri.EscapeDataString(draft.Name)}", draft, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkplaceDashboardDraft>(cancellationToken) ?? throw new AgentstrationApiException("Work API returned an empty Dashboard draft.", Guid.NewGuid().ToString("N"));
    }
    public async Task<WorkplaceDashboard> PublishDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/management/workspaces/{Uri.EscapeDataString(workspaceName)}/dashboards/{Uri.EscapeDataString(dashboardName)}/publish", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkplaceDashboard>(cancellationToken) ?? throw new AgentstrationApiException("Work API returned an empty published Dashboard.", Guid.NewGuid().ToString("N"));
    }
    public async Task DeleteDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"api/management/workspaces/{Uri.EscapeDataString(workspaceName)}/dashboards/{Uri.EscapeDataString(dashboardName)}", cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
}

