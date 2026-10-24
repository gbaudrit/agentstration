using Agentstration.Identity.Contracts;

namespace Agentstration.Identity;

public sealed class OperationalTopologyProvisioner(
    IIdentityStore store,
    IWorkspaceProvisioner workspaceProvisioner,
    TimeProvider timeProvider) : IOperationalTopologyProvisioner
{
    public async Task EnsureAllAsync(CancellationToken cancellationToken)
    {
        var systemTenant = await EnsureSystemTenantAsync(cancellationToken);
        _ = await EnsureWorkspaceAsync(
            systemTenant,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceId,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceName,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceDisplayName,
            cancellationToken);

        foreach (var tenant in (await store.ListTenantsAsync(cancellationToken))
                     .Where(value => value.Status == TenantStatus.Active
                         && !OperationalTopologyIdentities.IsSystemTenant(value.Id)))
            _ = await EnsureTenantAsync(tenant, cancellationToken);
    }

    public Task<Workspace> EnsureTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (OperationalTopologyIdentities.IsSystemTenant(tenant.Id))
            throw new InvalidOperationException("The reserved system Tenant uses the Instance operations Workspace.");
        if (tenant.Status != TenantStatus.Active)
            throw new InvalidOperationException($"Tenant '{tenant.Id:D}' is not active.");
        return EnsureWorkspaceAsync(
            tenant,
            OperationalTopologyIdentities.TenantOperationsWorkspaceId(tenant.Id),
            OperationalTopologyIdentities.TenantOperationsWorkspaceName,
            OperationalTopologyIdentities.TenantOperationsWorkspaceDisplayName,
            cancellationToken);
    }

    public async Task<Workspace> GetInstanceOperationsWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspace = await store.GetWorkspaceAsync(
            OperationalTopologyIdentities.SystemTenantId,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceId,
            cancellationToken);
        return workspace is { Status: WorkspaceStatus.Active }
            ? workspace
            : throw new InvalidOperationException("The Instance operations Workspace is unavailable.");
    }

    public async Task<Workspace> GetTenantOperationsWorkspaceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (OperationalTopologyIdentities.IsSystemTenant(tenantId))
            throw new InvalidOperationException("The reserved system Tenant does not have a Tenant operations Workspace.");
        var workspace = await store.GetWorkspaceAsync(
            tenantId,
            OperationalTopologyIdentities.TenantOperationsWorkspaceId(tenantId),
            cancellationToken);
        return workspace is { Status: WorkspaceStatus.Active }
            ? workspace
            : throw new InvalidOperationException($"The operations Workspace for Tenant '{tenantId:D}' is unavailable.");
    }

    private async Task<Tenant> EnsureSystemTenantAsync(CancellationToken cancellationToken)
    {
        var byId = await store.GetTenantAsync(OperationalTopologyIdentities.SystemTenantId, cancellationToken);
        var byName = await store.FindTenantByNameAsync(OperationalTopologyIdentities.SystemTenantName, cancellationToken);
        if (byId is not null && byName is not null && byId.Id != byName.Id)
            throw new InvalidOperationException("The reserved system Tenant name and identity refer to different Tenants.");
        if (byId is not null)
        {
            if (!string.Equals(byId.Name, OperationalTopologyIdentities.SystemTenantName, StringComparison.Ordinal))
                throw new InvalidOperationException("The reserved system Tenant identity is used by another Tenant.");
            if (byId.Status != TenantStatus.Active)
                throw new InvalidOperationException("The reserved system Tenant is disabled.");
            return byId;
        }
        if (byName is not null)
            throw new InvalidOperationException("The reserved system Tenant name is used by another Tenant.");

        var tenant = new Tenant(
            OperationalTopologyIdentities.SystemTenantId,
            OperationalTopologyIdentities.SystemTenantName,
            OperationalTopologyIdentities.SystemTenantDisplayName,
            TenantStatus.Active,
            timeProvider.GetUtcNow());
        await store.AddTenantAsync(tenant, cancellationToken);
        return tenant;
    }

    private async Task<Workspace> EnsureWorkspaceAsync(
        Tenant tenant,
        Guid workspaceId,
        string name,
        string displayName,
        CancellationToken cancellationToken)
    {
        var byId = await store.GetWorkspaceAsync(workspaceId, cancellationToken);
        if (byId is not null && byId.TenantId != tenant.Id)
            throw new InvalidOperationException($"The reserved Workspace identity '{workspaceId:D}' is used by another Tenant.");
        var byName = await store.FindWorkspaceByNameAsync(tenant.Id, name, cancellationToken);
        if (byId is not null && byName is not null && byId.Id != byName.Id)
            throw new InvalidOperationException($"The reserved Workspace name '{name}' and identity refer to different Workspaces.");
        var workspace = byId;
        if (workspace is not null)
        {
            if (!string.Equals(workspace.Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"The reserved Workspace identity '{workspaceId:D}' is used by another Workspace.");
            if (workspace.Status == WorkspaceStatus.Disabled)
                throw new InvalidOperationException($"The reserved Workspace '{name}' is disabled.");
        }
        else
        {
            if (byName is not null)
                throw new InvalidOperationException($"The reserved Workspace name '{name}' is used by another Workspace.");
            workspace = new Workspace(
                workspaceId,
                tenant.Id,
                name,
                displayName,
                WorkspaceStatus.Initializing,
                timeProvider.GetUtcNow());
            await store.AddWorkspaceAsync(workspace, cancellationToken);
        }

        if (workspace.Status == WorkspaceStatus.Initializing)
        {
            await workspaceProvisioner.ProvisionAsync(workspace, cancellationToken);
            workspace = await store.GetWorkspaceAsync(tenant.Id, workspace.Id, cancellationToken)
                ?? throw new InvalidOperationException($"The reserved Workspace '{name}' disappeared during provisioning.");
        }
        return workspace;
    }
}
