using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Identity;

public sealed record ReadableResourceScope(
    ResourceScopeRef ScopeRef,
    string DisplayName,
    Guid? TenantId);

public sealed class ReadableResourceScopeService(
    IIdentityStore identities,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization)
{
    public async Task<IReadOnlyList<ReadableResourceScope>> GetAsync(CancellationToken cancellationToken)
    {
        var accessMode = requestContext.AccessMode;
        if (accessMode == ControlPlaneAccessMode.Unavailable)
            return [];

        if (accessMode is ControlPlaneAccessMode.System or ControlPlaneAccessMode.Instance)
            return await GetAllAsync(cancellationToken);

        var current = requestContext.Current;
        if (accessMode is ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration)
        {
            var tenant = await identities.GetTenantAsync(current.TenantId, cancellationToken);
            if (tenant is null || tenant.Status != TenantStatus.Active
                || OperationalTopologyIdentities.IsSystemTenant(tenant))
                return [];
            var result = new List<ReadableResourceScope>
            {
                new(ResourceScopeRef.Tenant(tenant.Id), tenant.DisplayName, tenant.Id)
            };
            result.AddRange(await GetReadableWorkspacesAsync(
                current.PrincipalId,
                current.Restriction,
                [.. (await identities.ListWorkspacesAsync(tenant.Id, cancellationToken))],
                cancellationToken));
            return result;
        }

        var workspace = await identities.GetWorkspaceAsync(current.TenantId, current.WorkspaceId, cancellationToken);
        if (workspace is null || !IsUserWorkspace(workspace)
            || !await authorization.HasPermissionAsync(current, AuthorizationPermissions.ResourcesRead, cancellationToken))
            return [];
        return [new(ResourceScopeRef.Workspace(workspace.Id), workspace.DisplayName, workspace.TenantId)];
    }

    private async Task<IReadOnlyList<ReadableResourceScope>> GetAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<ReadableResourceScope>
        {
            new(ResourceScopeRef.Instance, string.Empty, null)
        };
        var tenants = (await identities.ListTenantsAsync(cancellationToken))
            .Where(tenant => tenant.Status == TenantStatus.Active
                && !OperationalTopologyIdentities.IsSystemTenant(tenant))
            .ToArray();
        var workspaces = await identities.ListWorkspacesAsync(cancellationToken);
        foreach (var tenant in tenants)
        {
            result.Add(new(ResourceScopeRef.Tenant(tenant.Id), tenant.DisplayName, tenant.Id));
            result.AddRange(workspaces
                .Where(workspace => workspace.TenantId == tenant.Id && IsUserWorkspace(workspace))
                .Select(workspace => new ReadableResourceScope(
                    ResourceScopeRef.Workspace(workspace.Id), workspace.DisplayName, workspace.TenantId)));
        }
        return result;
    }

    private async Task<IReadOnlyList<ReadableResourceScope>> GetReadableWorkspacesAsync(
        Guid principalId,
        AuthorizationRestriction? restriction,
        IReadOnlyList<Workspace> workspaces,
        CancellationToken cancellationToken)
    {
        var result = new List<ReadableResourceScope>();
        foreach (var workspace in workspaces.Where(IsUserWorkspace))
        {
            var context = new RequestContext(principalId, workspace.TenantId, workspace.Id, restriction);
            if (await authorization.HasPermissionAsync(context, AuthorizationPermissions.ResourcesRead, cancellationToken))
                result.Add(new(ResourceScopeRef.Workspace(workspace.Id), workspace.DisplayName, workspace.TenantId));
        }
        return result;
    }

    private static bool IsUserWorkspace(Workspace workspace) =>
        workspace.Status == WorkspaceStatus.Active
        && !OperationalTopologyIdentities.IsOperationalWorkspace(workspace);
}
