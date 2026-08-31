using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

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

