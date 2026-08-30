using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class PlatformAuthorizationService(IIdentityStore store) : IPlatformAuthorizationService
{
    public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
        store.IsPlatformAdministratorAsync(principalId, cancellationToken);
}

public sealed class LocalEnvironmentBootstrapper(
    IIdentityStore store,
    TimeProvider timeProvider,
    LocalBootstrapOptions options) : ILocalEnvironmentBootstrapper
{
    public static readonly Guid OwnerRoleId = new("65c86c44-4c42-4e33-91d4-2d8d13bdd681");

    public async Task<RequestContext> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tenant = await store.FindTenantByNameAsync(options.TenantName, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant(Guid.NewGuid(), options.TenantName, options.TenantDisplayName, TenantStatus.Active, now);
            await store.AddTenantAsync(tenant, cancellationToken);
        }

        var workspace = await store.FindWorkspaceByNameAsync(tenant.Id, options.WorkspaceName, cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace(Guid.NewGuid(), tenant.Id, options.WorkspaceName, options.WorkspaceDisplayName, WorkspaceStatus.Active, now);
            await store.AddWorkspaceAsync(workspace, cancellationToken);
        }

        var externalIdentity = await store.FindExternalIdentityAsync(options.ExternalIdentityIssuer, options.ExternalIdentitySubject, cancellationToken);
        Principal principal;
        if (externalIdentity is null)
        {
            principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, options.PrincipalDisplayName, null, PrincipalStatus.Active, now);
            await store.AddPrincipalAsync(principal, cancellationToken);
            externalIdentity = new ExternalIdentity(Guid.NewGuid(), options.ExternalIdentityIssuer, options.ExternalIdentitySubject, principal.Id, now);
            await store.AddExternalIdentityAsync(externalIdentity, cancellationToken);
        }
        else principal = await store.GetPrincipalAsync(externalIdentity.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The bootstrapped external identity references a missing principal.");

        if (await store.FindMembershipAsync(tenant.Id, principal.Id, cancellationToken) is null)
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), tenant.Id, principal.Id, MembershipStatus.Active, now), cancellationToken);
        if (await store.FindWorkspaceMembershipAsync(workspace.Id, principal.Id, cancellationToken) is null)
            await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, principal.Id, MembershipStatus.Active, now), cancellationToken);

        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        var owner = await store.FindRoleDefinitionByNameAsync(BuiltInIdentityRoles.Owner, cancellationToken)
            ?? throw new InvalidOperationException("The Owner role could not be initialized.");

        var assignments = await store.ListRoleAssignmentsAsync(tenant.Id, principal.Id, cancellationToken);
        var tenantScope = AuthorizationScopes.Tenant(tenant.Id);
        if (!assignments.Any(value => value.RoleDefinitionId == owner.Id && string.Equals(value.Scope, tenantScope, StringComparison.Ordinal)))
            await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), tenant.Id, principal.Id, PrincipalType.User, owner.Id, tenantScope), cancellationToken);

        return new RequestContext(principal.Id, tenant.Id, workspace.Id);
    }
}

public sealed class PermissionAuthorizationService(
    IIdentityStore store,
    IPlatformAuthorizationService platformAuthorization) : IAuthorizationService
{
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var principal = await store.GetPrincipalAsync(context.PrincipalId, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active) return new HashSet<string>(StringComparer.Ordinal);
        var workspace = await store.GetWorkspaceAsync(context.TenantId, context.WorkspaceId, cancellationToken);
        if (workspace?.Status != WorkspaceStatus.Active) return new HashSet<string>(StringComparer.Ordinal);
        var tenant = await store.GetTenantAsync(context.TenantId, cancellationToken);
        if (tenant?.Status != TenantStatus.Active) return new HashSet<string>(StringComparer.Ordinal);

        if (await platformAuthorization.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken))
        {
            var platformPermissions = new HashSet<string>(AuthorizationPermissions.All, StringComparer.Ordinal);
            ApplyRestriction(platformPermissions, context);
            return platformPermissions;
        }

        var membership = await store.FindWorkspaceMembershipAsync(context.WorkspaceId, context.PrincipalId, cancellationToken);
        if (membership?.Status != MembershipStatus.Active) return new HashSet<string>(StringComparer.Ordinal);

        var tenantScope = AuthorizationScopes.Tenant(context.TenantId);
        var workspaceScope = AuthorizationScopes.Workspace(context.WorkspaceId);
        var assignments = await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, cancellationToken);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in assignments.Where(value => value.PrincipalType == PrincipalType.User
                     && (string.Equals(value.Scope, tenantScope, StringComparison.Ordinal)
                         || string.Equals(value.Scope, workspaceScope, StringComparison.Ordinal))))
        {
            var role = await store.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
            if (role is not null) permissions.UnionWith(role.Permissions);
        }
        ApplyRestriction(permissions, context);
        return permissions;
    }

    private static void ApplyRestriction(HashSet<string> permissions, RequestContext context)
    {
        if (context.Restriction is not { } restriction) return;
        if (restriction.WorkspaceId != context.WorkspaceId)
            permissions.Clear();
        else
            permissions.IntersectWith(restriction.Permissions);
    }

    public async Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken) =>
        (await GetPermissionsAsync(context, cancellationToken)).Contains(permission);

    public async Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(context, permission, cancellationToken)) throw new AuthorizationDeniedException(permission);
    }
}

