using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public interface IIdentityAdministrationApiClient
{
    Task<IdentityConsoleContextResponse> GetContextAsync(CancellationToken cancellationToken);
    Task SelectWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<OrganizationAdministrationResponse> GetOrganizationAsync(CancellationToken cancellationToken);
    Task<Workspace> CreateWorkspaceAsync(string name, string displayName, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceMemberResponse>> GetWorkspaceMembersAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<WorkspaceMemberResponse> SetWorkspaceMembershipAsync(Guid workspaceId, Guid principalId, string role, CancellationToken cancellationToken);
    Task RemoveWorkspaceMembershipAsync(Guid workspaceId, Guid principalId, CancellationToken cancellationToken);
    Task<IdentityAdministrationCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformAdministratorResponse>> GetPlatformAdministratorsAsync(CancellationToken cancellationToken);
    Task GrantPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
    Task RevokePlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
    Task LinkExternalIdentityAsync(Guid principalId, string issuer, string subject, CancellationToken cancellationToken);
    Task UnlinkExternalIdentityAsync(Guid principalId, Guid externalIdentityId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityAuditEvent>> GetSecurityAuditEventsAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocalAccountResponse>> GetLocalAccountsAsync(CancellationToken cancellationToken);
    Task<LocalAccountResponse> CreateLocalAccountAsync(CreateLocalAccountRequest request, CancellationToken cancellationToken);
    Task<LocalAccountResponse> SetLocalAccountEnabledAsync(Guid accountId, bool enabled, CancellationToken cancellationToken);
}

public sealed class IdentityAdministrationApiClient(HttpClient httpClient) : IIdentityAdministrationApiClient
{
    public Task<IdentityConsoleContextResponse> GetContextAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<IdentityConsoleContextResponse>(httpClient, "api/identity/context", cancellationToken);

    public async Task SelectWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/identity/context/workspace",
            new SelectWorkspaceRequest(workspaceId),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<OrganizationAdministrationResponse> GetOrganizationAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<OrganizationAdministrationResponse>(httpClient, "api/identity/organization", cancellationToken);

    public async Task<Workspace> CreateWorkspaceAsync(string name, string displayName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/identity/workspaces",
            new CreateWorkspaceRequest(name, displayName),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<Workspace>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    public async Task<IReadOnlyList<WorkspaceMemberResponse>> GetWorkspaceMembersAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkspaceMemberResponse[]>(httpClient, $"api/identity/workspaces/{workspaceId:D}/memberships", cancellationToken);

    public async Task<WorkspaceMemberResponse> SetWorkspaceMembershipAsync(Guid workspaceId, Guid principalId, string role, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"api/identity/workspaces/{workspaceId:D}/memberships/{principalId:D}",
            new SetWorkspaceMembershipRequest(role),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkspaceMemberResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    public async Task RemoveWorkspaceMembershipAsync(Guid workspaceId, Guid principalId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"api/identity/workspaces/{workspaceId:D}/memberships/{principalId:D}", cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IdentityAdministrationCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<IdentityAdministrationCapabilitiesResponse>(httpClient, "api/identity/administration-capabilities", cancellationToken);

    public async Task<IReadOnlyList<PlatformAdministratorResponse>> GetPlatformAdministratorsAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<PlatformAdministratorResponse[]>(httpClient, "api/identity/platform-administrators", cancellationToken);

    public async Task GrantPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsync($"api/identity/platform-administrators/{principalId:D}", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RevokePlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"api/identity/platform-administrators/{principalId:D}", cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task LinkExternalIdentityAsync(Guid principalId, string issuer, string subject, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/identity/principals/{principalId:D}/external-identities",
            new LinkExternalIdentityRequest(issuer, subject),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UnlinkExternalIdentityAsync(Guid principalId, Guid externalIdentityId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(
            $"api/identity/principals/{principalId:D}/external-identities/{externalIdentityId:D}",
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<SecurityAuditEvent>> GetSecurityAuditEventsAsync(int limit, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<SecurityAuditEvent[]>(httpClient, $"api/identity/audit-events?limit={limit}", cancellationToken);

    public async Task<IReadOnlyList<LocalAccountResponse>> GetLocalAccountsAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<LocalAccountResponse[]>(httpClient, "api/identity/accounts/", cancellationToken);

    public async Task<LocalAccountResponse> CreateLocalAccountAsync(CreateLocalAccountRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/identity/accounts/", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LocalAccountResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    public async Task<LocalAccountResponse> SetLocalAccountEnabledAsync(Guid accountId, bool enabled, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"api/identity/accounts/{accountId:D}/status",
            new SetLocalAccountStatusRequest(enabled),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LocalAccountResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }
}
