using Agentstration.Identity.Contracts;
using Agentstration.Security.AspNetCoreIdentity;

namespace Agentstration.Identity.Api.Security;

public sealed record BffLocalSessionAuthorityResult(
    LocalLoginOutcome Outcome,
    BffSessionIdentityResponse? Identity = null);

public sealed class BffSessionAuthorityService(
    LocalAuthenticationService localAuthentication,
    IIdentityStore identities,
    IPlatformAuthorizationService platformAuthorization,
    Agentstration.Identity.Contracts.IAuthorizationService authorization)
{
    public async Task<BffLocalSessionAuthorityResult> AuthenticateLocalAsync(
        BffLocalSessionRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await localAuthentication.ValidateCredentialsAsync(
            request.UserName,
            request.Password,
            cancellationToken);
        if (validation.Outcome != LocalLoginOutcome.Succeeded
            || validation.PrincipalId is not { } principalId
            || string.IsNullOrWhiteSpace(validation.AuthenticationVersion))
            return new(validation.Outcome);

        var identity = await ResolveAsync(
            principalId,
            BffSessionAuthenticationMethods.Local,
            "local",
            validation.AuthenticationVersion,
            null,
            null,
            cancellationToken);
        return identity is null
            ? new(LocalLoginOutcome.Failed)
            : new(LocalLoginOutcome.Succeeded, identity);
    }

    public async Task<BffSessionValidationResponse> ValidateAsync(
        BffSessionValidationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.AuthenticationMethod, BffSessionAuthenticationMethods.Local, StringComparison.Ordinal)
            || !string.Equals(request.Provider, "local", StringComparison.Ordinal)
            || !await localAuthentication.ValidateSessionAsync(
                request.PrincipalId,
                request.AuthenticationVersion,
                cancellationToken))
            return new(false, null, "authentication-unavailable");
        var identity = await ResolveAsync(
            request.PrincipalId,
            BffSessionAuthenticationMethods.Local,
            "local",
            request.AuthenticationVersion,
            request.TenantId,
            request.WorkspaceId,
            cancellationToken);
        return identity is null
            ? new(false, null, "principal-or-context-unavailable")
            : new(true, identity);
    }

    private async Task<BffSessionIdentityResponse?> ResolveAsync(
        Guid principalId,
        string authenticationMethod,
        string provider,
        string authenticationVersion,
        Guid? requestedTenantId,
        Guid? requestedWorkspaceId,
        CancellationToken cancellationToken)
    {
        var principal = await identities.GetPrincipalAsync(principalId, cancellationToken);
        if (principal is not { Status: PrincipalStatus.Active, Kind: PrincipalKind.Human }) return null;

        var candidates = new List<Workspace>();
        if (await platformAuthorization.IsPlatformAdministratorAsync(principalId, cancellationToken))
        {
            foreach (var tenant in (await identities.ListTenantsAsync(cancellationToken))
                         .Where(value => value.Status == TenantStatus.Active))
                candidates.AddRange((await identities.ListWorkspacesAsync(tenant.Id, cancellationToken))
                    .Where(value => value.Status == WorkspaceStatus.Active));
        }
        else
        {
            foreach (var membership in (await identities.ListWorkspaceMembershipsAsync(principalId, cancellationToken))
                         .Where(value => value.Status == MembershipStatus.Active))
            {
                var workspace = await identities.GetWorkspaceAsync(membership.WorkspaceId, cancellationToken);
                if (workspace is not { Status: WorkspaceStatus.Active }) continue;
                var tenant = await identities.GetTenantAsync(workspace.TenantId, cancellationToken);
                if (tenant?.Status == TenantStatus.Active) candidates.Add(workspace);
            }
        }

        var readable = new List<Workspace>();
        foreach (var workspace in candidates.DistinctBy(value => value.Id))
        {
            var context = new RequestContext(principalId, workspace.TenantId, workspace.Id);
            if (await authorization.HasPermissionAsync(context, AuthorizationPermissions.WorkspacesRead, cancellationToken))
                readable.Add(workspace);
        }
        if (readable.Count == 0) return null;

        Workspace? selected = null;
        if (requestedWorkspaceId is { } workspaceId)
            selected = readable.SingleOrDefault(value => value.Id == workspaceId
                && (requestedTenantId is null || value.TenantId == requestedTenantId));
        if (requestedWorkspaceId is not null && selected is null) return null;

        if (selected is null)
        {
            var preferences = await identities.GetPrincipalPreferencesAsync(principalId, cancellationToken);
            selected = readable.SingleOrDefault(value => value.Id == preferences?.DefaultWorkspaceId
                && (preferences?.DefaultTenantId is null || value.TenantId == preferences.DefaultTenantId));
        }
        selected ??= readable.OrderBy(value => value.TenantId).ThenBy(value => value.Name, StringComparer.Ordinal).First();

        return new(
            principal.Id,
            principal.DisplayName,
            authenticationMethod,
            provider,
            selected.TenantId,
            selected.Id,
            authenticationVersion);
    }
}
