using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

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

