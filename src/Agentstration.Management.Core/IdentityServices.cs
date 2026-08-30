using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class LocalBootstrapOptions
{
    public const string DevelopmentIssuer = "https://agentstration.local/development";
    public const string DevelopmentSubject = "local-operator";
    public string TenantName { get; set; } = "dev";
    public string TenantDisplayName { get; set; } = "Development";
    public string WorkspaceName { get; set; } = "default";
    public string WorkspaceDisplayName { get; set; } = "Default workspace";
    public string PrincipalDisplayName { get; set; } = "Development operator";
    public string ExternalIdentityIssuer { get; set; } = DevelopmentIssuer;
    public string ExternalIdentitySubject { get; set; } = DevelopmentSubject;
}

public sealed class CurrentRequestContext : ICurrentRequestContext, IRequestContextScopeFactory
{
    private readonly AsyncLocal<AmbientRequestContext?> ambient = new();
    public bool IsInitialized => AccessMode == ControlPlaneAccessMode.Workspace;
    public ControlPlaneAccessMode AccessMode => ambient.Value?.AccessMode ?? ControlPlaneAccessMode.Unavailable;
    public RequestContext Current => ambient.Value switch
    {
        { AccessMode: ControlPlaneAccessMode.Workspace, Context: not null } value => value.Context,
        { AccessMode: ControlPlaneAccessMode.System } => throw new InvalidOperationException("System operations do not have a workspace request context."),
        _ => throw new InvalidOperationException("The request context has not been initialized.")
    };
    public IDisposable Push(RequestContext context) => Push(new AmbientRequestContext(ControlPlaneAccessMode.Workspace, context));
    public IDisposable PushSystem() => Push(new AmbientRequestContext(ControlPlaneAccessMode.System, null));

    private IDisposable Push(AmbientRequestContext context)
    {
        var previous = ambient.Value;
        ambient.Value = context;
        return new Scope(() => ambient.Value = previous);
    }

    private sealed record AmbientRequestContext(ControlPlaneAccessMode AccessMode, RequestContext? Context);

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}

public sealed class ExternalIdentityPrincipalResolver(IIdentityStore store) : IPrincipalResolver
{
    public async Task<Principal?> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return null;
        var identity = await store.FindExternalIdentityAsync(issuer, subject, cancellationToken);
        return identity is null ? null : await store.GetPrincipalAsync(identity.PrincipalId, cancellationToken);
    }

    public async Task<Principal?> ResolveLocalAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var identity = await store.FindLocalIdentityAsync(accountId, cancellationToken);
        return identity is null ? null : await store.GetPrincipalAsync(identity.PrincipalId, cancellationToken);
    }
}

public sealed class InitialPrincipalProvisioner(
    IIdentityStore store,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider) : IInitialPrincipalProvisioner
{
    public async Task<InitialPrincipalProvisioningResult> ProvisionAsync(InitialPrincipalProvisioning request, CancellationToken cancellationToken)
    {
        if (await store.FindLocalIdentityAsync(request.AccountId, cancellationToken) is not null)
            throw new InvalidOperationException("The local account is already linked to an Agentstration principal.");

        var now = timeProvider.GetUtcNow();
        var principal = new Principal(request.PrincipalId, PrincipalKind.Human, request.DisplayName, request.Email, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(principal, cancellationToken);
        await store.AddLocalIdentityAsync(new LocalIdentity(request.AccountId, principal.Id, now), cancellationToken);
        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        await store.AddPlatformAdministratorAsync(new PlatformAdministrator(principal.Id, now), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.PlatformAdministratorGranted,
            ActorPrincipalId: principal.Id,
            TargetPrincipalId: principal.Id,
            TargetAccountId: request.AccountId), cancellationToken);
        return new InitialPrincipalProvisioningResult(principal);
    }
}

public sealed class InitialTopologyProvisioner(
    IIdentityStore store,
    TimeProvider timeProvider) : IInitialTopologyProvisioner
{
    public async Task<InitialTopologyProvisioningResult> ProvisionAsync(
        InitialTopologyProvisioning request,
        CancellationToken cancellationToken)
    {
        var principal = await store.GetPrincipalAsync(request.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The initial Principal does not exist.");
        if (!await store.IsPlatformAdministratorAsync(principal.Id, cancellationToken))
            throw new InvalidOperationException("The initial Principal is not a Platform administrator.");

        var tenantName = IdentityBootstrapValidation.Name(request.TenantName, "Tenant");
        var tenantDisplayName = IdentityBootstrapValidation.DisplayName(request.TenantDisplayName, "Tenant");
        var workspaceName = IdentityBootstrapValidation.Name(request.WorkspaceName, "Workspace");
        var workspaceDisplayName = IdentityBootstrapValidation.DisplayName(request.WorkspaceDisplayName, "Workspace");
        var now = timeProvider.GetUtcNow();

        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant(Guid.NewGuid(), tenantName, tenantDisplayName, TenantStatus.Active, now);
            await store.AddTenantAsync(tenant, cancellationToken);
        }

        var workspace = await store.FindWorkspaceByNameAsync(tenant.Id, workspaceName, cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace(Guid.NewGuid(), tenant.Id, workspaceName, workspaceDisplayName, WorkspaceStatus.Active, now);
            await store.AddWorkspaceAsync(workspace, cancellationToken);
        }

        var preferences = await store.GetPrincipalPreferencesAsync(principal.Id, cancellationToken)
            ?? new PrincipalPreferences(principal.Id, ThemePreference.System, now);
        await store.UpsertPrincipalPreferencesAsync(preferences with
        {
            DefaultTenantId = tenant.Id,
            DefaultWorkspaceId = workspace.Id,
            UpdatedAt = now
        }, cancellationToken);
        return new InitialTopologyProvisioningResult(tenant, workspace);
    }
}

internal static class IdentityBootstrapValidation
{
    public static string Name(string value, string resource)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length is < 2 or > 64
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException($"{resource} names must contain 2-64 lowercase letters, digits, or hyphens.");
        return value;
    }

    public static string DisplayName(string value, string resource)
    {
        value = value.Trim();
        if (value.Length is < 2 or > 120)
            throw new ArgumentException($"{resource} display names must contain 2-120 characters.");
        return value;
    }
}

public static class BuiltInIdentityRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> Names = [Owner, Admin, Member, Viewer];

    private static readonly IReadOnlyList<RoleDefinition> Definitions =
    [
        new(LocalEnvironmentBootstrapper.OwnerRoleId, Owner, Owner, AuthorizationPermissions.All, true),
        new(new Guid("ef263a5e-1f83-4882-bef4-b76847574fe2"), Admin, Admin,
            [AuthorizationPermissions.TenantsRead, AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.WorkspacesWrite,
             AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete,
             AuthorizationPermissions.RunsRead, AuthorizationPermissions.RunsExecute, AuthorizationPermissions.AuthorizationRead,
             AuthorizationPermissions.AuthorizationWrite], true),
        new(new Guid("2c0b9724-f78f-43db-b0b6-673c04dc68a4"), Member, Member,
            [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.RunsRead, AuthorizationPermissions.RunsExecute], true),
        new(new Guid("8bb015ea-acda-4770-8d7a-0399e1d28ab4"), Viewer, Viewer,
            [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.RunsRead], true)
    ];

    public static async Task EnsureAsync(IIdentityStore store, CancellationToken cancellationToken)
    {
        foreach (var definition in Definitions)
            if (await store.FindRoleDefinitionByNameAsync(definition.Name, cancellationToken) is null)
                await store.AddRoleDefinitionAsync(definition, cancellationToken);
    }
}

public sealed class LocalPrincipalProvisioner(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService platformAuthorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider) : ILocalPrincipalProvisioner
{
    public async Task<Principal> ProvisionAsync(LocalPrincipalProvisioning request, CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized || !await platformAuthorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        if (await store.FindLocalIdentityAsync(request.AccountId, cancellationToken) is not null)
            throw new InvalidOperationException("The local account is already linked.");
        var workspace = await store.GetWorkspaceAsync(request.WorkspaceId, cancellationToken)
            ?? throw new ArgumentException("The target Workspace does not exist.", nameof(request));
        if (workspace.Status != WorkspaceStatus.Active) throw new ArgumentException("The target Workspace is disabled.", nameof(request));
        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        var role = await store.FindRoleDefinitionByNameAsync(request.Role, cancellationToken);
        if (role is null || !BuiltInIdentityRoles.Names.Contains(role.Name, StringComparer.Ordinal))
            throw new ArgumentException("The requested role is not a supported built-in Workspace role.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var principal = new Principal(request.PrincipalId, PrincipalKind.Human, request.DisplayName, request.Email, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(principal, cancellationToken);
        if (await store.FindMembershipAsync(workspace.TenantId, principal.Id, cancellationToken) is null)
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), workspace.TenantId, principal.Id, MembershipStatus.Active, now), cancellationToken);
        await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, principal.Id, MembershipStatus.Active, now), cancellationToken);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), workspace.TenantId, principal.Id, PrincipalType.User, role.Id, AuthorizationScopes.Workspace(workspace.Id)), cancellationToken);
        await store.AddLocalIdentityAsync(new LocalIdentity(request.AccountId, principal.Id, now), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.WorkspaceMembershipSet,
            TargetPrincipalId: principal.Id,
            TargetAccountId: request.AccountId,
            TenantId: workspace.TenantId,
            WorkspaceId: workspace.Id,
            ReasonCode: $"role-{role.Name.ToLowerInvariant()}"), cancellationToken);
        return principal;
    }
}

public sealed record WorkspaceMemberView(Principal Principal, WorkspaceMembership Membership, string? Role, bool Inherited);

public sealed class WorkspaceMembershipAdministrationService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<WorkspaceMemberView>> ListAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await RequireWorkspaceAccessAsync(workspaceId, AuthorizationPermissions.AuthorizationRead, cancellationToken);
        var result = new List<WorkspaceMemberView>();
        foreach (var membership in await store.ListWorkspaceMembersAsync(workspace.Id, cancellationToken))
        {
            var principal = await store.GetPrincipalAsync(membership.PrincipalId, cancellationToken);
            if (principal is null) continue;
            var role = await EffectiveRoleAsync(workspace, principal.Id, cancellationToken);
            result.Add(new WorkspaceMemberView(principal, membership, role.Role?.Name, role.Inherited));
        }
        return result.OrderBy(value => value.Principal.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<WorkspaceMemberView> SetAsync(Guid workspaceId, Guid principalId, string roleName, CancellationToken cancellationToken)
    {
        var workspace = await RequireWorkspaceAccessAsync(workspaceId, AuthorizationPermissions.AuthorizationWrite, cancellationToken);
        await BuiltInIdentityRoles.EnsureAsync(store, cancellationToken);
        var role = await store.FindRoleDefinitionByNameAsync(roleName, cancellationToken);
        if (role is null || !BuiltInIdentityRoles.Names.Contains(role.Name, StringComparer.Ordinal))
            throw new ArgumentException("Role must be Owner, Admin, Member, or Viewer.", nameof(roleName));
        var principal = await store.GetPrincipalAsync(principalId, cancellationToken)
            ?? throw new ArgumentException("The Principal does not exist.", nameof(principalId));
        if (principal.Status != PrincipalStatus.Active) throw new ArgumentException("The Principal is disabled.", nameof(principalId));

        var existingRole = await EffectiveRoleAsync(workspace, principalId, cancellationToken);
        if (existingRole.Role?.Name == BuiltInIdentityRoles.Owner && role.Name != BuiltInIdentityRoles.Owner
            && await CountOwnersAsync(workspace, cancellationToken) <= 1)
            throw new InvalidOperationException("The last Owner of a Workspace cannot be demoted.");

        var now = timeProvider.GetUtcNow();
        var membership = await store.FindWorkspaceMembershipAsync(workspace.Id, principal.Id, cancellationToken);
        if (membership is null)
        {
            membership = new WorkspaceMembership(Guid.NewGuid(), workspace.Id, principal.Id, MembershipStatus.Active, now);
            await store.AddWorkspaceMembershipAsync(membership, cancellationToken);
        }
        else if (membership.Status != MembershipStatus.Active)
        {
            membership = membership with { Status = MembershipStatus.Active };
            await store.UpdateWorkspaceMembershipAsync(membership, cancellationToken);
        }
        if (await store.FindMembershipAsync(workspace.TenantId, principal.Id, cancellationToken) is null)
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), workspace.TenantId, principal.Id, MembershipStatus.Active, now), cancellationToken);

        var scope = AuthorizationScopes.Workspace(workspace.Id);
        foreach (var assignment in (await store.ListRoleAssignmentsAsync(workspace.TenantId, principal.Id, cancellationToken))
                     .Where(value => string.Equals(value.Scope, scope, StringComparison.Ordinal)))
            await store.RemoveRoleAssignmentAsync(assignment.Id, cancellationToken);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), workspace.TenantId, principal.Id, PrincipalType.User, role.Id, scope), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.WorkspaceMembershipSet,
            TargetPrincipalId: principal.Id,
            TenantId: workspace.TenantId,
            WorkspaceId: workspace.Id,
            ReasonCode: $"role-{role.Name.ToLowerInvariant()}"), cancellationToken);
        return new WorkspaceMemberView(principal, membership, role.Name, false);
    }

    public async Task RemoveAsync(Guid workspaceId, Guid principalId, CancellationToken cancellationToken)
    {
        var workspace = await RequireWorkspaceAccessAsync(workspaceId, AuthorizationPermissions.AuthorizationWrite, cancellationToken);
        var membership = await store.FindWorkspaceMembershipAsync(workspace.Id, principalId, cancellationToken);
        if (membership is null) return;
        var effective = await EffectiveRoleAsync(workspace, principalId, cancellationToken);
        if (effective.Role?.Name == BuiltInIdentityRoles.Owner && await CountOwnersAsync(workspace, cancellationToken) <= 1)
            throw new InvalidOperationException("The last Owner of a Workspace cannot be removed.");
        var scope = AuthorizationScopes.Workspace(workspace.Id);
        foreach (var assignment in (await store.ListRoleAssignmentsAsync(workspace.TenantId, principalId, cancellationToken))
                     .Where(value => string.Equals(value.Scope, scope, StringComparison.Ordinal)))
            await store.RemoveRoleAssignmentAsync(assignment.Id, cancellationToken);
        await store.RemoveWorkspaceMembershipAsync(membership.Id, cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.WorkspaceMembershipRemoved,
            TargetPrincipalId: principalId,
            TenantId: workspace.TenantId,
            WorkspaceId: workspace.Id), cancellationToken);
    }

    private async Task<Workspace> RequireWorkspaceAccessAsync(Guid workspaceId, string permission, CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized || requestContext.Current.WorkspaceId != workspaceId)
            throw new AuthorizationDeniedException(permission);
        await authorization.EnsurePermissionAsync(requestContext.Current, permission, cancellationToken);
        return await store.GetWorkspaceAsync(requestContext.Current.TenantId, workspaceId, cancellationToken)
            ?? throw new ArgumentException("The Workspace does not exist.", nameof(workspaceId));
    }

    private async Task<(RoleDefinition? Role, bool Inherited)> EffectiveRoleAsync(Workspace workspace, Guid principalId, CancellationToken cancellationToken)
    {
        var assignments = await store.ListRoleAssignmentsAsync(workspace.TenantId, principalId, cancellationToken);
        foreach (var candidate in new[] { (AuthorizationScopes.Workspace(workspace.Id), false), (AuthorizationScopes.Tenant(workspace.TenantId), true) })
        {
            foreach (var assignment in assignments.Where(value => string.Equals(value.Scope, candidate.Item1, StringComparison.Ordinal)))
            {
                var role = await store.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
                if (role is not null) return (role, candidate.Item2);
            }
        }
        return (null, false);
    }

    private async Task<int> CountOwnersAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var membership in (await store.ListWorkspaceMembersAsync(workspace.Id, cancellationToken)).Where(value => value.Status == MembershipStatus.Active))
            if ((await EffectiveRoleAsync(workspace, membership.PrincipalId, cancellationToken)).Role?.Name == BuiltInIdentityRoles.Owner) count++;
        return count;
    }
}

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

public sealed record TenantAdministrationView(
    Tenant Tenant,
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<MemberAdministrationView> Members);

public sealed record MemberAdministrationView(Principal Principal, TenantMembership Membership, IReadOnlyList<ExternalIdentity> ExternalIdentities, IReadOnlyList<AssignedRoleView> Roles);
public sealed record AssignedRoleView(RoleDefinition Role, string Scope);

public sealed record ConsoleWorkspaceView(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    string Name,
    string DisplayName,
    WorkspaceStatus Status,
    IReadOnlyList<string> Permissions);
public sealed record ConsoleContextView(
    RequestContext Context,
    string UserDisplayName,
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ConsoleWorkspaceView> AvailableWorkspaces);

public sealed class IdentityAdministrationService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    TimeProvider timeProvider)
{
    public async Task<TenantAdministrationView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var context = requestContext.Current;
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.AuthorizationRead, cancellationToken);
        var tenant = await store.GetTenantAsync(context.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("The current tenant no longer exists.");
        var workspaces = await store.ListWorkspacesAsync(context.TenantId, cancellationToken);
        var memberships = await store.ListMembershipsAsync(context.TenantId, cancellationToken);
        var members = new List<MemberAdministrationView>();
        foreach (var membership in memberships)
        {
            var principal = await store.GetPrincipalAsync(membership.PrincipalId, cancellationToken);
            if (principal is null) continue;
            var assignments = await store.ListRoleAssignmentsAsync(context.TenantId, principal.Id, cancellationToken);
            var roles = new List<AssignedRoleView>();
            foreach (var assignment in assignments)
            {
                var role = await store.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
                if (role is not null) roles.Add(new AssignedRoleView(role, assignment.Scope));
            }
            members.Add(new MemberAdministrationView(principal, membership, await store.ListExternalIdentitiesAsync(principal.Id, cancellationToken), roles));
        }
        return new TenantAdministrationView(tenant, workspaces, members);
    }

    public async Task<Workspace> CreateWorkspaceAsync(string name, string displayName, CancellationToken cancellationToken)
    {
        var context = requestContext.Current;
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.WorkspacesWrite, cancellationToken);
        name = name.Trim().ToLowerInvariant();
        displayName = displayName.Trim();
        if (name.Length is < 2 or > 64 || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Workspace names must contain 2-64 lowercase letters, digits, or hyphens.", nameof(name));
        if (displayName.Length is < 2 or > 120) throw new ArgumentException("Workspace display names must contain 2-120 characters.", nameof(displayName));
        if (await store.FindWorkspaceByNameAsync(context.TenantId, name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Workspace '{name}' already exists in the current tenant.");
        var now = timeProvider.GetUtcNow();
        var workspace = new Workspace(Guid.NewGuid(), context.TenantId, name, displayName, WorkspaceStatus.Active, now);
        await store.AddWorkspaceAsync(workspace, cancellationToken);
        if (!await store.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken))
            await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, context.PrincipalId, MembershipStatus.Active, now), cancellationToken);
        return workspace;
    }
}

public sealed class IdentityExperienceService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization)
{
    public async Task<ConsoleContextView> GetContextAsync(CancellationToken cancellationToken)
    {
        var context = requestContext.Current;
        var principal = await store.GetPrincipalAsync(context.PrincipalId, cancellationToken) ?? throw new InvalidOperationException("The current principal no longer exists.");
        var available = new List<ConsoleWorkspaceView>();
        var tenants = await platformAuthorization.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken)
            ? await store.ListTenantsAsync(cancellationToken)
            : (await store.GetTenantAsync(context.TenantId, cancellationToken)) is { } currentTenant
                ? [currentTenant]
                : throw new InvalidOperationException("The current tenant no longer exists.");
        foreach (var tenant in tenants.Where(value => value.Status == TenantStatus.Active))
        {
            var workspaces = await store.ListWorkspacesAsync(tenant.Id, cancellationToken);
            foreach (var workspace in workspaces.Where(value => value.Status == WorkspaceStatus.Active))
            {
                var candidate = context with { TenantId = tenant.Id, WorkspaceId = workspace.Id };
                var permissions = await authorization.GetPermissionsAsync(candidate, cancellationToken);
                if (permissions.Contains(AuthorizationPermissions.WorkspacesRead))
                    available.Add(new ConsoleWorkspaceView(
                        workspace.Id,
                        tenant.Id,
                        tenant.Name,
                        tenant.DisplayName,
                        workspace.Name,
                        workspace.DisplayName,
                        workspace.Status,
                        permissions.Order(StringComparer.Ordinal).ToArray()));
            }
        }
        var currentWorkspace = available.SingleOrDefault(value => value.Id == context.WorkspaceId)
            ?? available.FirstOrDefault()
            ?? throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        var selectedContext = context with { TenantId = currentWorkspace.TenantId, WorkspaceId = currentWorkspace.Id };
        return new ConsoleContextView(
            selectedContext,
            principal.DisplayName,
            currentWorkspace.TenantName,
            currentWorkspace.TenantDisplayName,
            currentWorkspace.Name,
            currentWorkspace.DisplayName,
            currentWorkspace.Permissions,
            available);
    }

    public async Task<RequestContext> ValidateWorkspaceSelectionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await store.GetWorkspaceAsync(workspaceId, cancellationToken)
            ?? throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        var context = requestContext.Current with { TenantId = workspace.TenantId, WorkspaceId = workspaceId };
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.WorkspacesRead, cancellationToken);
        return context;
    }
}
