using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed record ResourceScopeInventoryNode(
    ResourceScopeRef ScopeRef,
    ResourceScopeKind Kind,
    string DisplayName,
    ResourceScopeRef? ParentScopeRef,
    bool IsCurrent,
    IReadOnlyList<ResourceInventoryEntry> Resources);

public sealed class ResourceScopeInventoryService(
    IdentityExperienceService identityExperience,
    IResourceStore store,
    IRequestContextScopeFactory scopeFactory)
{
    public async Task<IReadOnlyList<ResourceScopeInventoryNode>> GetAsync(CancellationToken cancellationToken)
    {
        var contextView = await identityExperience.GetContextAsync(cancellationToken);
        var current = contextView.Context;
        var result = new List<ResourceScopeInventoryNode>
        {
            new(
                ResourceScopeRef.Instance,
                ResourceScopeKind.Instance,
                string.Empty,
                null,
                false,
                await ListAllAsync(ResourceScopeRef.Instance, cancellationToken))
        };

        foreach (var tenantGroup in contextView.AvailableWorkspaces
                     .Where(workspace => workspace.Permissions.Contains(AuthorizationPermissions.ResourcesRead, StringComparer.Ordinal))
                     .GroupBy(workspace => new { workspace.TenantId, workspace.TenantDisplayName })
                     .OrderBy(group => group.Key.TenantDisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var firstWorkspace = tenantGroup.First();
            var tenantContext = current with
            {
                TenantId = tenantGroup.Key.TenantId,
                WorkspaceId = firstWorkspace.Id
            };
            using (scopeFactory.Push(tenantContext))
            {
                result.Add(new(
                    ResourceScopeRef.Tenant(tenantGroup.Key.TenantId),
                    ResourceScopeKind.Tenant,
                    tenantGroup.Key.TenantDisplayName,
                    ResourceScopeRef.Instance,
                    false,
                    await ListAllAsync(ResourceScopeRef.Tenant(tenantGroup.Key.TenantId), cancellationToken)));
            }

            foreach (var workspace in tenantGroup.OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
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
                        ResourceScopeRef.Tenant(workspace.TenantId),
                        current.WorkspaceId == workspace.Id,
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
        const int pageSize = 500;
        var resources = new List<ResourceInventoryEntry>();
        while (true)
        {
            var page = await store.ListExactInventoryAsync(scopeRef, resources.Count, pageSize, cancellationToken);
            resources.AddRange(page);
            if (page.Count < pageSize) return resources;
        }
    }
}
