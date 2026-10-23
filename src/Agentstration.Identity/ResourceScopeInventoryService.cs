using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Identity;

public sealed record ResourceScopeInventoryNode(
    ResourceScopeRef ScopeRef,
    ResourceScopeKind Kind,
    string DisplayName,
    ResourceScopeRef? ParentScopeRef,
    bool IsCurrent,
    IReadOnlyList<ResourceInventoryEntry> Resources);

public sealed class ResourceScopeInventoryService(
    IdentityExperienceService identityExperience,
    IResourceIdentityCatalog identities,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory scopeFactory)
{
    public async Task<IReadOnlyList<ResourceScopeInventoryNode>> GetAsync(CancellationToken cancellationToken)
    {
        var contextView = await identityExperience.GetContextAsync(cancellationToken);
        var current = contextView.Context;
        var accessMode = requestContext.AccessMode;
        var result = new List<ResourceScopeInventoryNode>();

        if (accessMode == ControlPlaneAccessMode.Instance)
        {
            result.Add(new(
                ResourceScopeRef.Instance,
                ResourceScopeKind.Instance,
                string.Empty,
                null,
                true,
                await ListAllAsync(ResourceScopeRef.Instance, cancellationToken)));
        }

        foreach (var tenantGroup in contextView.AvailableWorkspaces
                     .Where(workspace => accessMode == ControlPlaneAccessMode.Instance
                         || workspace.TenantId == current.TenantId)
                     .Where(workspace => workspace.Permissions.Contains(AuthorizationPermissions.ResourcesRead, StringComparer.Ordinal))
                     .GroupBy(workspace => new { workspace.TenantId, workspace.TenantDisplayName })
                     .OrderBy(group => group.Key.TenantDisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var firstWorkspace = tenantGroup.First();
            var tenantScopeRef = ResourceScopeRef.Tenant(tenantGroup.Key.TenantId);
            var includeTenant = accessMode is ControlPlaneAccessMode.Instance
                or ControlPlaneAccessMode.Tenant
                or ControlPlaneAccessMode.TenantAdministration;
            var tenantContext = current with
            {
                TenantId = tenantGroup.Key.TenantId,
                WorkspaceId = firstWorkspace.Id
            };
            if (includeTenant)
            {
                using var tenantTarget = accessMode == ControlPlaneAccessMode.Instance
                    ? scopeFactory.PushTenant(current.PrincipalId, tenantGroup.Key.TenantId)
                    : null;
                result.Add(new(
                    tenantScopeRef,
                    ResourceScopeKind.Tenant,
                    tenantGroup.Key.TenantDisplayName,
                    accessMode == ControlPlaneAccessMode.Instance ? ResourceScopeRef.Instance : null,
                    accessMode is ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration,
                    await ListAllAsync(tenantScopeRef, cancellationToken)));
            }

            foreach (var workspace in tenantGroup.OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (accessMode == ControlPlaneAccessMode.Workspace && workspace.Id != current.WorkspaceId)
                    continue;
                var workspaceContext = current with
                {
                    TenantId = workspace.TenantId,
                    WorkspaceId = workspace.Id
                };
                using (scopeFactory.Push(workspaceContext))
                {
                    result.Add(new(
                        ResourceScopeRef.Workspace(workspace.Id),
                        ResourceScopeKind.Workspace,
                        workspace.DisplayName,
                        includeTenant ? ResourceScopeRef.Tenant(workspace.TenantId) : null,
                        accessMode == ControlPlaneAccessMode.Workspace && current.WorkspaceId == workspace.Id,
                        await ListAllAsync(ResourceScopeRef.Workspace(workspace.Id), cancellationToken)));
                }
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<ResourceInventoryEntry>> ListAllAsync(
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        return await identities.ListExactInventoryAsync(scopeRef, cancellationToken);
    }
}
