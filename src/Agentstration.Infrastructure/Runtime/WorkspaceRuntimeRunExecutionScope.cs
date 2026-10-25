using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class WorkspaceRuntimeRunExecutionScope(
    IIdentityStore identities,
    Agentstration.Identity.Contracts.IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization,
    IRequestContextScopeFactory scopeFactory) : IRuntimeRunExecutionScope
{
    public async ValueTask ValidateAsync(RuntimeRunScope scope, CancellationToken cancellationToken)
        => await ValidateAsync(scope, AuthorizationPermissions.RunsExecute, cancellationToken);

    public async ValueTask ValidateReadAsync(RuntimeRunScope scope, CancellationToken cancellationToken)
        => await ValidateAsync(scope, AuthorizationPermissions.RunsRead, cancellationToken);

    private async ValueTask ValidateAsync(
        RuntimeRunScope scope,
        string permission,
        CancellationToken cancellationToken)
    {
        var principal = await identities.GetPrincipalAsync(scope.PrincipalId, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active) throw Denied();
        if (scope.AccessScopeRef.Kind == ResourceScopeKind.Instance)
        {
            if (!await platformAuthorization.IsPlatformAdministratorAsync(scope.PrincipalId, cancellationToken)) throw Denied();
            return;
        }
        var tenant = scope.TenantId == Guid.Empty ? null : await identities.GetTenantAsync(scope.TenantId, cancellationToken);
        if (tenant?.Status != TenantStatus.Active || scope.ScopeRef.TargetId != scope.TenantId && scope.ScopeRef.Kind == ResourceScopeKind.Tenant)
            throw Denied();
        if (scope.AccessScopeRef.Kind == ResourceScopeKind.Tenant)
        {
            if (await platformAuthorization.IsPlatformAdministratorAsync(scope.PrincipalId, cancellationToken)) return;
            var membership = await identities.FindMembershipAsync(scope.TenantId, scope.PrincipalId, cancellationToken);
            if (membership?.Status != MembershipStatus.Active || !await HasTenantPermissionAsync(scope, permission, cancellationToken)) throw Denied();
            return;
        }
        var workspace = await identities.GetWorkspaceAsync(scope.TenantId, scope.WorkspaceId.Value, cancellationToken);
        if (workspace?.Status != WorkspaceStatus.Active || scope.AccessScopeRef != ResourceScopeRef.Workspace(scope.WorkspaceId.Value)) throw Denied();
        try { await authorization.EnsurePermissionAsync(new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value), permission, cancellationToken); }
        catch (AuthorizationDeniedException) { throw Denied(); }
    }

    public IDisposable Enter(RuntimeRunScope scope)
    {
        return scope.AccessScopeRef.Kind switch
        {
            ResourceScopeKind.Instance => scopeFactory.PushSystem(),
            ResourceScopeKind.Tenant => scopeFactory.PushTenant(scope.PrincipalId, scope.TenantId),
            ResourceScopeKind.Workspace => scopeFactory.Push(new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value)),
            _ => throw Denied()
        };
    }

    private async Task<bool> HasTenantPermissionAsync(RuntimeRunScope scope, string permission, CancellationToken cancellationToken)
    {
        var assignments = await identities.ListRoleAssignmentsAsync(scope.TenantId, scope.PrincipalId, cancellationToken);
        foreach (var assignment in assignments.Where(value => string.Equals(value.Scope, AuthorizationScopes.Tenant(scope.TenantId), StringComparison.Ordinal)))
        {
            var role = await identities.GetRoleDefinitionAsync(assignment.RoleDefinitionId, cancellationToken);
            if (role?.Permissions.Contains(permission) == true) return true;
        }
        return false;
    }

    private static RuntimeRunValidationException Denied() =>
        new("runtime_run_authorization_denied", "The Principal is no longer authorized to execute this Runtime Run in its exact scope.");
}
