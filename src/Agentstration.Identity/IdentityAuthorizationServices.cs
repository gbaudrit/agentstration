using Agentstration.Identity.Contracts;

namespace Agentstration.Identity;

public sealed class PlatformAuthorizationService(IIdentityStore store) : IPlatformAuthorizationService
{
    public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
        store.IsPlatformAdministratorAsync(principalId, cancellationToken);
}

public sealed class LocalEnvironmentBootstrapper(
    IIdentityStore store,
    TimeProvider timeProvider,
    LocalBootstrapOptions options,
    IWorkspaceProvisioner workspaceProvisioner) : ILocalEnvironmentBootstrapper
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
            workspace = new Workspace(Guid.NewGuid(), tenant.Id, options.WorkspaceName, options.WorkspaceDisplayName, WorkspaceStatus.Initializing, now);
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

        if (options.GrantPlatformAdministrator
            && !await store.IsPlatformAdministratorAsync(principal.Id, cancellationToken))
            await store.AddPlatformAdministratorAsync(new PlatformAdministrator(principal.Id, now), cancellationToken);

        if (workspace.Status == WorkspaceStatus.Initializing)
        {
            await workspaceProvisioner.ProvisionAsync(workspace, cancellationToken);
            workspace = workspace with { Status = WorkspaceStatus.Active };
        }

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
        var tenant = await store.GetTenantAsync(context.TenantId, cancellationToken);
        if (tenant?.Status != TenantStatus.Active) return new HashSet<string>(StringComparer.Ordinal);

        if (await platformAuthorization.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken))
            return ResolvePermissions(context, true, false, false, [], new Dictionary<Guid, RoleDefinition>());

        var tenantOnly = context.WorkspaceId == Guid.Empty;
        var hasTenantMembership = false;
        var hasWorkspaceMembership = false;
        if (tenantOnly)
        {
            var tenantMembership = await store.FindMembershipAsync(context.TenantId, context.PrincipalId, cancellationToken);
            hasTenantMembership = tenantMembership?.Status == MembershipStatus.Active;
        }
        else
        {
            var workspace = await store.GetWorkspaceAsync(context.TenantId, context.WorkspaceId, cancellationToken);
            if (workspace?.Status != WorkspaceStatus.Active) return new HashSet<string>(StringComparer.Ordinal);
            var membership = await store.FindWorkspaceMembershipAsync(context.WorkspaceId, context.PrincipalId, cancellationToken);
            hasWorkspaceMembership = membership?.Status == MembershipStatus.Active;
        }
        if (tenantOnly ? !hasTenantMembership : !hasWorkspaceMembership)
            return new HashSet<string>(StringComparer.Ordinal);

        var assignments = await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, cancellationToken);
        var roles = (await store.ListRoleDefinitionsAsync(cancellationToken)).ToDictionary(value => value.Id);
        return ResolvePermissions(context, false, hasTenantMembership, hasWorkspaceMembership, assignments, roles);
    }

    internal static IReadOnlySet<string> ResolvePermissions(
        RequestContext context,
        bool isPlatformAdministrator,
        bool hasTenantMembership,
        bool hasWorkspaceMembership,
        IReadOnlyList<RoleAssignment> assignments,
        IReadOnlyDictionary<Guid, RoleDefinition> roles)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        if (isPlatformAdministrator)
            permissions.UnionWith(AuthorizationPermissions.All);
        else
        {
            var tenantOnly = context.WorkspaceId == Guid.Empty;
            if (tenantOnly ? !hasTenantMembership : !hasWorkspaceMembership) return permissions;
            var tenantScope = AuthorizationScopes.Tenant(context.TenantId);
            var workspaceScope = AuthorizationScopes.Workspace(context.WorkspaceId);
            foreach (var assignment in assignments.Where(value => value.TenantId == context.TenantId
                         && value.PrincipalType == PrincipalType.User
                         && (string.Equals(value.Scope, tenantScope, StringComparison.Ordinal)
                             || !tenantOnly && string.Equals(value.Scope, workspaceScope, StringComparison.Ordinal))))
                if (roles.TryGetValue(assignment.RoleDefinitionId, out var role))
                    permissions.UnionWith(role.Permissions);
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

