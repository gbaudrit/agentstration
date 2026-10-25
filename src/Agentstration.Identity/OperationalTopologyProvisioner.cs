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
            WorkspaceKind.InstanceOperations,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceName,
            OperationalTopologyIdentities.InstanceOperationsWorkspaceDisplayName,
            cancellationToken);

        foreach (var tenant in (await store.ListTenantsAsync(cancellationToken))
                     .Where(value => value.Status == TenantStatus.Active
                         && !OperationalTopologyIdentities.IsSystemTenant(value)))
            _ = await EnsureTenantAsync(tenant, cancellationToken);
    }

    public Task<Workspace> EnsureTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (OperationalTopologyIdentities.IsSystemTenant(tenant))
            throw new InvalidOperationException("The reserved system Tenant uses the Instance operations Workspace.");
        if (tenant.Status != TenantStatus.Active)
            throw new InvalidOperationException($"Tenant '{tenant.Id:D}' is not active.");
        return EnsureWorkspaceAsync(
            tenant,
            WorkspaceKind.TenantOperations,
            OperationalTopologyIdentities.TenantOperationsWorkspaceName,
            OperationalTopologyIdentities.TenantOperationsWorkspaceDisplayName,
            cancellationToken);
    }

    public async Task<Workspace> GetInstanceOperationsWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspace = (await store.ListWorkspacesAsync(cancellationToken))
            .SingleOrDefault(value => value.Kind == WorkspaceKind.InstanceOperations);
        return workspace is { Status: WorkspaceStatus.Active }
            ? workspace
            : throw new InvalidOperationException("The Instance operations Workspace is unavailable.");
    }

    public async Task<Workspace> GetTenantOperationsWorkspaceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await store.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is null || OperationalTopologyIdentities.IsSystemTenant(tenant))
            throw new InvalidOperationException("The reserved system Tenant does not have a Tenant operations Workspace.");
        var workspace = (await store.ListWorkspacesAsync(tenantId, cancellationToken))
            .SingleOrDefault(value => value.Kind == WorkspaceKind.TenantOperations);
        return workspace is { Status: WorkspaceStatus.Active }
            ? workspace
            : throw new InvalidOperationException($"The operations Workspace for Tenant '{tenantId:D}' is unavailable.");
    }

    private async Task<Tenant> EnsureSystemTenantAsync(CancellationToken cancellationToken)
    {
        var byKind = (await store.ListTenantsAsync(cancellationToken))
            .SingleOrDefault(value => value.Kind == TenantKind.System);
        var byName = await store.FindTenantByNameAsync(OperationalTopologyIdentities.SystemTenantName, cancellationToken);
        if (byKind is not null && byName is not null && byKind.Id != byName.Id)
            throw new InvalidOperationException("The reserved system Tenant name and identity refer to different Tenants.");
        if (byKind is not null)
        {
            if (!string.Equals(byKind.Name, OperationalTopologyIdentities.SystemTenantName, StringComparison.Ordinal))
                throw new InvalidOperationException("The system Tenant kind is used by another Tenant.");
            if (byKind.Status != TenantStatus.Active)
                throw new InvalidOperationException("The reserved system Tenant is disabled.");
            return byKind;
        }
        if (byName is not null)
            throw new InvalidOperationException("The reserved system Tenant name is used by another Tenant.");

        var tenant = new Tenant(
            Guid.NewGuid(),
            OperationalTopologyIdentities.SystemTenantName,
            OperationalTopologyIdentities.SystemTenantDisplayName,
            TenantStatus.Active,
            timeProvider.GetUtcNow(),
            TenantKind.System);
        await store.AddTenantAsync(tenant, cancellationToken);
        return tenant;
    }

    private async Task<Workspace> EnsureWorkspaceAsync(
        Tenant tenant,
        WorkspaceKind kind,
        string name,
        string displayName,
        CancellationToken cancellationToken)
    {
        var byKind = (await store.ListWorkspacesAsync(tenant.Id, cancellationToken))
            .SingleOrDefault(value => value.Kind == kind);
        var byName = await store.FindWorkspaceByNameAsync(tenant.Id, name, cancellationToken);
        if (byKind is not null && byName is not null && byKind.Id != byName.Id)
            throw new InvalidOperationException($"The reserved Workspace name '{name}' and identity refer to different Workspaces.");
        var workspace = byKind;
        if (workspace is not null)
        {
            if (!string.Equals(workspace.Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"The Workspace kind '{kind}' is used by another Workspace.");
            if (workspace.Status == WorkspaceStatus.Disabled)
                throw new InvalidOperationException($"The reserved Workspace '{name}' is disabled.");
        }
        else
        {
            if (byName is not null)
                throw new InvalidOperationException($"The reserved Workspace name '{name}' is used by another Workspace.");
            workspace = new Workspace(
                Guid.NewGuid(),
                tenant.Id,
                name,
                displayName,
                WorkspaceStatus.Initializing,
                timeProvider.GetUtcNow(),
                kind);
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
