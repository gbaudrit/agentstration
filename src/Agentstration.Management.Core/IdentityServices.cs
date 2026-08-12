using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class LocalBootstrapOptions
{
    public string TenantName { get; set; } = "local";
    public string TenantDisplayName { get; set; } = "Local organization";
    public string WorkspaceName { get; set; } = "default";
    public string WorkspaceDisplayName { get; set; } = "Default workspace";
    public string UserDisplayName { get; set; } = "Local User";
}

public sealed class CurrentRequestContext : ICurrentRequestContext, IRequestContextInitializer, IRequestContextScopeFactory
{
    private readonly AsyncLocal<RequestContext?> ambient = new();
    private RequestContext? fallback;
    public bool IsInitialized => ambient.Value is not null || fallback is not null;
    public RequestContext Current => ambient.Value ?? fallback ?? throw new InvalidOperationException("The request context has not been initialized.");
    public void Initialize(RequestContext context) => fallback = context;
    public IDisposable Push(RequestContext context)
    {
        var previous = ambient.Value;
        ambient.Value = context;
        return new Scope(() => ambient.Value = previous);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}

public sealed class LocalIdentityProvider(IIdentityStore store) : IIdentityProvider
{
    public const string LocalSubject = "agentstration:local";

    public async Task<User> ResolveCurrentUserAsync(CancellationToken cancellationToken) =>
        await store.FindUserByExternalSubjectAsync(LocalSubject, cancellationToken)
        ?? throw new InvalidOperationException("The local user has not been bootstrapped.");
}

public sealed class LocalEnvironmentBootstrapper(
    IIdentityStore store,
    IIdentityProvider identityProvider,
    IRequestContextInitializer contextInitializer,
    IResourceScopeMigrator resourceMigrator,
    TimeProvider timeProvider,
    LocalBootstrapOptions options) : ILocalEnvironmentBootstrapper
{
    public static readonly Guid OwnerRoleId = new("65c86c44-4c42-4e33-91d4-2d8d13bdd681");

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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

        var user = await store.FindUserByExternalSubjectAsync(LocalIdentityProvider.LocalSubject, cancellationToken);
        if (user is null)
        {
            user = new User(Guid.NewGuid(), LocalIdentityProvider.LocalSubject, options.UserDisplayName, null, UserStatus.Active, now);
            await store.AddUserAsync(user, cancellationToken);
        }

        if (await store.FindMembershipAsync(tenant.Id, user.Id, cancellationToken) is null)
            await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), tenant.Id, user.Id, MembershipStatus.Active, now), cancellationToken);

        var owner = await store.FindRoleDefinitionByNameAsync("Owner", cancellationToken);
        if (owner is null)
        {
            owner = new RoleDefinition(OwnerRoleId, "Owner", "Owner", AuthorizationPermissions.All, true);
            await store.AddRoleDefinitionAsync(owner, cancellationToken);
        }

        var assignments = await store.ListRoleAssignmentsAsync(tenant.Id, user.Id, cancellationToken);
        var tenantScope = AuthorizationScopes.Tenant(tenant.Id);
        if (!assignments.Any(value => value.RoleDefinitionId == owner.Id && string.Equals(value.Scope, tenantScope, StringComparison.Ordinal)))
            await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), tenant.Id, user.Id, PrincipalType.User, owner.Id, tenantScope), cancellationToken);

        var resolvedUser = await identityProvider.ResolveCurrentUserAsync(cancellationToken);
        contextInitializer.Initialize(new RequestContext(resolvedUser.Id, tenant.Id, workspace.Id));
        await resourceMigrator.BackfillUnscopedResourcesAsync(tenant.Id, workspace.Id, cancellationToken);
    }
}

public sealed class PermissionAuthorizationService(IIdentityStore store) : IAuthorizationService
{
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var membership = await store.FindMembershipAsync(context.TenantId, context.UserId, cancellationToken);
        if (membership?.Status != MembershipStatus.Active) return new HashSet<string>(StringComparer.Ordinal);
        if (await store.GetWorkspaceAsync(context.TenantId, context.WorkspaceId, cancellationToken) is null) return new HashSet<string>(StringComparer.Ordinal);

        var tenantScope = AuthorizationScopes.Tenant(context.TenantId);
        var workspaceScope = AuthorizationScopes.Workspace(context.WorkspaceId);
        var assignments = await store.ListRoleAssignmentsAsync(context.TenantId, context.UserId, cancellationToken);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in assignments.Where(value => value.PrincipalType == PrincipalType.User
                     && (string.Equals(value.Scope, tenantScope, StringComparison.Ordinal)
                         || string.Equals(value.Scope, workspaceScope, StringComparison.Ordinal))))
        {
            var role = await store.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
            if (role is not null) permissions.UnionWith(role.Permissions);
        }
        return permissions;
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

public sealed record MemberAdministrationView(User User, TenantMembership Membership, IReadOnlyList<AssignedRoleView> Roles);
public sealed record AssignedRoleView(RoleDefinition Role, string Scope);

public sealed record ConsoleWorkspaceView(Guid Id, string Name, string DisplayName, WorkspaceStatus Status, IReadOnlyList<string> Permissions);
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
            var user = await store.GetUserAsync(membership.UserId, cancellationToken);
            if (user is null) continue;
            var assignments = await store.ListRoleAssignmentsAsync(context.TenantId, user.Id, cancellationToken);
            var roles = new List<AssignedRoleView>();
            foreach (var assignment in assignments)
            {
                var role = await store.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
                if (role is not null) roles.Add(new AssignedRoleView(role, assignment.Scope));
            }
            members.Add(new MemberAdministrationView(user, membership, roles));
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
        return workspace;
    }
}

public sealed class IdentityExperienceService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization)
{
    public async Task<ConsoleContextView> GetContextAsync(CancellationToken cancellationToken)
    {
        var context = requestContext.Current;
        var tenant = await store.GetTenantAsync(context.TenantId, cancellationToken) ?? throw new InvalidOperationException("The current tenant no longer exists.");
        var user = await store.GetUserAsync(context.UserId, cancellationToken) ?? throw new InvalidOperationException("The current user no longer exists.");
        var workspaces = await store.ListWorkspacesAsync(context.TenantId, cancellationToken);
        var available = new List<ConsoleWorkspaceView>();
        foreach (var workspace in workspaces.Where(value => value.Status == WorkspaceStatus.Active))
        {
            var candidate = context with { WorkspaceId = workspace.Id };
            var permissions = await authorization.GetPermissionsAsync(candidate, cancellationToken);
            if (permissions.Contains(AuthorizationPermissions.WorkspacesRead))
                available.Add(new ConsoleWorkspaceView(workspace.Id, workspace.Name, workspace.DisplayName, workspace.Status, permissions.Order(StringComparer.Ordinal).ToArray()));
        }
        var currentWorkspace = available.SingleOrDefault(value => value.Id == context.WorkspaceId)
            ?? available.FirstOrDefault()
            ?? throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        var selectedContext = context with { WorkspaceId = currentWorkspace.Id };
        return new ConsoleContextView(
            selectedContext,
            user.DisplayName,
            tenant.Name,
            tenant.DisplayName,
            currentWorkspace.Name,
            currentWorkspace.DisplayName,
            currentWorkspace.Permissions,
            available);
    }

    public async Task<RequestContext> ValidateWorkspaceSelectionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var context = requestContext.Current with { WorkspaceId = workspaceId };
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.WorkspacesRead, cancellationToken);
        return context;
    }
}
