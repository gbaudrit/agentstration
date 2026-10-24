using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Identity;

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
public sealed record ConsoleContextOptionView(
    ResourceScopeRef ScopeRef,
    IdentityConsoleContextKind Kind,
    Guid? TenantId,
    string? TenantName,
    string? TenantDisplayName,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string DisplayName);
public sealed record ConsoleContextView(
    RequestContext Context,
    ResourceScopeRef SelectedScopeRef,
    IdentityConsoleContextKind SelectedKind,
    string UserDisplayName,
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ConsoleWorkspaceView> AvailableWorkspaces,
    IReadOnlyList<ConsoleContextOptionView> AvailableContexts);

public sealed record ConsoleContextSelection(
    ResourceScopeRef ScopeRef,
    IdentityConsoleContextKind Kind,
    RequestContext OperationalContext);

public sealed class IdentityAdministrationService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    TimeProvider timeProvider,
    IWorkspaceProvisioner workspaceProvisioner)
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
            throw new ResourceConcurrencyException($"Workspace '{name}' already exists in the current tenant.");
        var now = timeProvider.GetUtcNow();
        var workspace = new Workspace(Guid.NewGuid(), context.TenantId, name, displayName, WorkspaceStatus.Initializing, now);
        await store.AddWorkspaceAsync(workspace, cancellationToken);
        if (!await store.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken))
            await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), workspace.Id, context.PrincipalId, MembershipStatus.Active, now), cancellationToken);
        await workspaceProvisioner.ProvisionAsync(workspace, cancellationToken);
        return workspace with { Status = WorkspaceStatus.Active };
    }
}

public sealed class IdentityExperienceService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization)
{
    public Task<ConsoleContextView> GetContextAsync(CancellationToken cancellationToken) =>
        GetContextAsync(null, cancellationToken);

    public async Task<ConsoleContextView> GetContextAsync(
        ResourceScopeRef? selectedScopeRef,
        CancellationToken cancellationToken)
    {
        var context = requestContext.Current;
        var principal = await store.GetPrincipalAsync(context.PrincipalId, cancellationToken) ?? throw new InvalidOperationException("The current principal no longer exists.");
        var available = new List<ConsoleWorkspaceView>();
        var tenantPermissions = new Dictionary<Guid, IReadOnlySet<string>>();
        var isPlatformAdministrator = await platformAuthorization.IsPlatformAdministratorAsync(context.PrincipalId, cancellationToken);
        var tenants = await store.ListTenantsAsync(cancellationToken);
        var workspaces = await store.ListWorkspacesAsync(cancellationToken);
        IReadOnlyList<TenantMembership> tenantMemberships = isPlatformAdministrator
            ? []
            : await store.ListTenantMembershipsAsync(context.PrincipalId, cancellationToken);
        IReadOnlyList<WorkspaceMembership> workspaceMemberships = isPlatformAdministrator
            ? []
            : await store.ListWorkspaceMembershipsAsync(context.PrincipalId, cancellationToken);
        IReadOnlyList<RoleAssignment> assignments = isPlatformAdministrator
            ? []
            : await store.ListRoleAssignmentsAsync(context.PrincipalId, cancellationToken);
        IReadOnlyDictionary<Guid, RoleDefinition> roles = isPlatformAdministrator
            ? new Dictionary<Guid, RoleDefinition>()
            : (await store.ListRoleDefinitionsAsync(cancellationToken)).ToDictionary(value => value.Id);
        foreach (var tenant in tenants.Where(value => value.Status == TenantStatus.Active))
        {
            tenantPermissions[tenant.Id] = ResolvePermissions(
                context with { TenantId = tenant.Id, WorkspaceId = Guid.Empty },
                isPlatformAdministrator, tenantMemberships, workspaceMemberships, assignments, roles);
            foreach (var workspace in workspaces.Where(value => value.TenantId == tenant.Id && value.Status == WorkspaceStatus.Active))
            {
                var candidate = context with { TenantId = tenant.Id, WorkspaceId = workspace.Id };
                var permissions = ResolvePermissions(
                    candidate, isPlatformAdministrator, tenantMemberships, workspaceMemberships, assignments, roles);
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
        var contexts = BuildAvailableContexts(available, tenantPermissions, isPlatformAdministrator);
        var selected = selectedScopeRef is { } requested
            ? contexts.SingleOrDefault(value => value.ScopeRef == requested)
            : null;
        selected ??= contexts.Single(value => value.ScopeRef == ResourceScopeRef.Workspace(currentWorkspace.Id));
        var operationalWorkspace = selected.WorkspaceId is { } workspaceId
            ? available.Single(value => value.Id == workspaceId)
            : selected.TenantId is { } tenantId
                ? available.First(value => value.TenantId == tenantId)
                : currentWorkspace;
        var selectedContext = context with
        {
            TenantId = operationalWorkspace.TenantId,
            WorkspaceId = operationalWorkspace.Id
        };
        IReadOnlyList<string> selectedPermissions = selected.Kind switch
        {
            IdentityConsoleContextKind.Instance => InstanceAdministrativePermissions.Order(StringComparer.Ordinal).ToArray(),
            IdentityConsoleContextKind.Tenant => tenantPermissions[selected.TenantId!.Value]
                .Where(TenantAdministrativePermissions.Contains).Order(StringComparer.Ordinal).ToArray(),
            _ => operationalWorkspace.Permissions
        };
        return new ConsoleContextView(
            selectedContext,
            selected.ScopeRef,
            selected.Kind,
            principal.DisplayName,
            operationalWorkspace.TenantName,
            operationalWorkspace.TenantDisplayName,
            operationalWorkspace.Name,
            operationalWorkspace.DisplayName,
            selectedPermissions,
            available,
            contexts);
    }

    public async Task<ConsoleContextSelection> ValidateConsoleContextSelectionAsync(
        Guid principalId,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var current = requestContext.Current;
        if (current.PrincipalId != principalId)
            throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        return scopeRef.Kind switch
        {
            ResourceScopeKind.Instance => await ValidateInstanceSelectionAsync(
                principalId, current, scopeRef, cancellationToken),
            ResourceScopeKind.Tenant => await ValidateTenantSelectionAsync(
                principalId, current, scopeRef, cancellationToken),
            ResourceScopeKind.Workspace => new(scopeRef, IdentityConsoleContextKind.Workspace,
                await ValidateWorkspaceSelectionAsync(principalId, scopeRef.TargetId!.Value, cancellationToken)),
            _ => throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead)
        };
    }

    public async Task<RequestContext> ValidateWorkspaceSelectionAsync(Guid workspaceId, CancellationToken cancellationToken)
        => await ValidateWorkspaceSelectionAsync(requestContext.Current.PrincipalId, workspaceId, cancellationToken);

    public async Task<RequestContext> ValidateWorkspaceSelectionAsync(
        Guid principalId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var workspace = await store.GetWorkspaceAsync(workspaceId, cancellationToken)
            ?? throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        var context = new RequestContext(principalId, workspace.TenantId, workspaceId);
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.WorkspacesRead, cancellationToken);
        return context;
    }

    private static IReadOnlyList<ConsoleContextOptionView> BuildAvailableContexts(
        IReadOnlyList<ConsoleWorkspaceView> workspaces,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> tenantPermissions,
        bool isPlatformAdministrator)
    {
        var contexts = new List<ConsoleContextOptionView>();
        if (isPlatformAdministrator)
            contexts.Add(new(ResourceScopeRef.Instance, IdentityConsoleContextKind.Instance,
                null, null, null, null, null, "Instance"));
        foreach (var tenant in workspaces
                     .GroupBy(value => new { value.TenantId, value.TenantName, value.TenantDisplayName })
                     .OrderBy(value => value.Key.TenantDisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (isPlatformAdministrator || tenantPermissions[tenant.Key.TenantId].Contains(AuthorizationPermissions.TenantsRead))
                contexts.Add(new(ResourceScopeRef.Tenant(tenant.Key.TenantId), IdentityConsoleContextKind.Tenant,
                    tenant.Key.TenantId, tenant.Key.TenantName, tenant.Key.TenantDisplayName,
                    null, null, tenant.Key.TenantDisplayName));
            contexts.AddRange(tenant
                .OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(workspace => new ConsoleContextOptionView(
                    ResourceScopeRef.Workspace(workspace.Id), IdentityConsoleContextKind.Workspace,
                    workspace.TenantId, workspace.TenantName, workspace.TenantDisplayName,
                    workspace.Id, workspace.Name, workspace.DisplayName)));
        }
        return contexts;
    }

    private async Task<ConsoleContextSelection> ValidateInstanceSelectionAsync(
        Guid principalId,
        RequestContext operationalContext,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        if (!await platformAuthorization.IsPlatformAdministratorAsync(principalId, cancellationToken))
            throw new AuthorizationDeniedException(AuthorizationPermissions.TenantsRead);
        return new(scopeRef, IdentityConsoleContextKind.Instance, operationalContext);
    }

    private async Task<ConsoleContextSelection> ValidateTenantSelectionAsync(
        Guid principalId,
        RequestContext current,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var tenantId = scopeRef.TargetId!.Value;
        var tenant = await store.GetTenantAsync(tenantId, cancellationToken);
        if (tenant?.Status != TenantStatus.Active)
            throw new AuthorizationDeniedException(AuthorizationPermissions.TenantsRead);
        var isPlatformAdministrator = await platformAuthorization.IsPlatformAdministratorAsync(principalId, cancellationToken);
        if (!isPlatformAdministrator)
        {
            var tenantContext = new RequestContext(principalId, tenantId, Guid.Empty, current.Restriction);
            await authorization.EnsurePermissionAsync(tenantContext, AuthorizationPermissions.TenantsRead, cancellationToken);
        }
        var workspaces = await store.ListWorkspacesAsync(tenantId, cancellationToken);
        Workspace? operationalWorkspace;
        if (isPlatformAdministrator)
        {
            operationalWorkspace = workspaces
                .Where(value => value.Status == WorkspaceStatus.Active)
                .OrderByDescending(value => value.Id == current.WorkspaceId)
                .FirstOrDefault();
        }
        else
        {
            var memberships = await store.ListWorkspaceMembershipsAsync(principalId, cancellationToken);
            var assignments = await store.ListRoleAssignmentsAsync(principalId, cancellationToken);
            var roles = (await store.ListRoleDefinitionsAsync(cancellationToken)).ToDictionary(value => value.Id);
            operationalWorkspace = workspaces
                .Where(value => value.Status == WorkspaceStatus.Active)
                .OrderByDescending(value => value.Id == current.WorkspaceId)
                .FirstOrDefault(value => ResolvePermissions(
                    new RequestContext(principalId, tenantId, value.Id, current.Restriction),
                    false, [], memberships, assignments, roles)
                    .Contains(AuthorizationPermissions.WorkspacesRead));
        }
        if (operationalWorkspace is null)
            throw new AuthorizationDeniedException(AuthorizationPermissions.WorkspacesRead);
        return new(scopeRef, IdentityConsoleContextKind.Tenant,
            new RequestContext(principalId, tenantId, operationalWorkspace.Id, current.Restriction));
    }

    private static IReadOnlySet<string> ResolvePermissions(
        RequestContext context,
        bool isPlatformAdministrator,
        IReadOnlyList<TenantMembership> tenantMemberships,
        IReadOnlyList<WorkspaceMembership> workspaceMemberships,
        IReadOnlyList<RoleAssignment> assignments,
        IReadOnlyDictionary<Guid, RoleDefinition> roles)
        => PermissionAuthorizationService.ResolvePermissions(
            context,
            isPlatformAdministrator,
            tenantMemberships.Any(value => value.TenantId == context.TenantId && value.Status == MembershipStatus.Active),
            workspaceMemberships.Any(value => value.WorkspaceId == context.WorkspaceId && value.Status == MembershipStatus.Active),
            assignments,
            roles);

    private static readonly IReadOnlySet<string> InstanceAdministrativePermissions = new HashSet<string>(
        [
            AuthorizationPermissions.TenantsRead,
            AuthorizationPermissions.ResourcesRead,
            AuthorizationPermissions.ResourcesWrite,
            AuthorizationPermissions.ResourcesDelete,
            AuthorizationPermissions.AuthorizationRead,
            AuthorizationPermissions.AuthorizationWrite
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TenantAdministrativePermissions = new HashSet<string>(
        [
            AuthorizationPermissions.TenantsRead,
            AuthorizationPermissions.TenantsManage,
            AuthorizationPermissions.WorkspacesRead,
            AuthorizationPermissions.WorkspacesWrite,
            AuthorizationPermissions.WorkspacesDelete,
            AuthorizationPermissions.ResourcesRead,
            AuthorizationPermissions.ResourcesWrite,
            AuthorizationPermissions.ResourcesDelete,
            AuthorizationPermissions.AuthorizationRead,
            AuthorizationPermissions.AuthorizationWrite
        ], StringComparer.Ordinal);
}

