using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed record TenantBootstrapDefinition
{
    public string DisplayName { get; init; } = string.Empty;
}

public sealed record BootstrapResourceNameReference
{
    public string Name { get; init; } = string.Empty;
}

public sealed record WorkspaceBootstrapDefinition
{
    public string DisplayName { get; init; } = string.Empty;
    public BootstrapResourceNameReference TenantRef { get; init; } = new();
}

public sealed record LocalAccountBootstrapReference
{
    public string LocalAccount { get; init; } = string.Empty;
}

public sealed record PrincipalDefaultContextBootstrapDefinition
{
    public LocalAccountBootstrapReference PrincipalRef { get; init; } = new();
    public BootstrapResourceNameReference TenantRef { get; init; } = new();
    public BootstrapResourceNameReference WorkspaceRef { get; init; } = new();
}

public sealed class TenantBootstrapResourceHandler(
    IIdentityStore store,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.Tenant;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var (name, _) = Read(resource);
        if (await store.FindTenantByNameAsync(name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        planning.Register(Kind, name);
        return new(BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var (name, displayName) = Read(resource);
        if (await store.FindTenantByNameAsync(name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;

        await store.AddTenantAsync(
            new Tenant(Guid.NewGuid(), name, displayName, TenantStatus.Active, timeProvider.GetUtcNow()),
            cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static (string Name, string DisplayName) Read(BootstrapResourceDocument resource)
    {
        var name = IdentityBootstrapValidation.Name(resource.Metadata.Name, "Tenant");
        var definition = resource.Definition.Deserialize<TenantBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("Tenant definition is required.");
        return (name, IdentityBootstrapValidation.DisplayName(definition.DisplayName, "Tenant"));
    }
}

public sealed class WorkspaceBootstrapResourceHandler(
    IIdentityStore store,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.Workspace;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var (name, _, tenantName) = Read(resource);
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken);
        if (tenant is null)
        {
            if (!planning.Contains(BootstrapResourceKinds.Tenant, tenantName))
                throw new InvalidOperationException($"Workspace '{name}' references missing Tenant '{tenantName}'.");
            planning.Register(Kind, name, tenantName);
            return new(BootstrapResourceDisposition.Create);
        }
        if (await store.FindWorkspaceByNameAsync(tenant.Id, name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        planning.Register(Kind, name, tenantName);
        return new(BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var (name, displayName, tenantName) = Read(resource);
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace '{name}' references missing Tenant '{tenantName}'.");
        if (await store.FindWorkspaceByNameAsync(tenant.Id, name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;

        await store.AddWorkspaceAsync(
            new Workspace(Guid.NewGuid(), tenant.Id, name, displayName, WorkspaceStatus.Active, timeProvider.GetUtcNow()),
            cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static (string Name, string DisplayName, string TenantName) Read(BootstrapResourceDocument resource)
    {
        var name = IdentityBootstrapValidation.Name(resource.Metadata.Name, "Workspace");
        var definition = resource.Definition.Deserialize<WorkspaceBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("Workspace definition is required.");
        return (
            name,
            IdentityBootstrapValidation.DisplayName(definition.DisplayName, "Workspace"),
            IdentityBootstrapValidation.Name(definition.TenantRef.Name, "Tenant"));
    }
}

public sealed class PrincipalDefaultContextBootstrapResourceHandler(
    IIdentityStore store,
    ILocalAccountPrincipalResolver principals,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.PrincipalDefaultContext;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var (resourceName, userName, tenantName, workspaceName) = Read(resource);
        var principal = await principals.ResolveByUserNameAsync(userName, cancellationToken);
        if (principal is null && !planning.Contains(BootstrapResourceKinds.PlatformAdministrator, userName))
            throw new InvalidOperationException($"PrincipalDefaultContext references missing local account '{userName}'.");
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken);
        if (tenant is null && !planning.Contains(BootstrapResourceKinds.Tenant, tenantName))
            throw new InvalidOperationException($"PrincipalDefaultContext references missing Tenant '{tenantName}'.");
        if (tenant is not null && await store.FindWorkspaceByNameAsync(tenant.Id, workspaceName, cancellationToken) is null
            && !planning.Contains(BootstrapResourceKinds.Workspace, workspaceName, tenantName))
            throw new InvalidOperationException($"PrincipalDefaultContext references missing Workspace '{tenantName}/{workspaceName}'.");
        if (tenant is null && !planning.Contains(BootstrapResourceKinds.Workspace, workspaceName, tenantName))
            throw new InvalidOperationException($"PrincipalDefaultContext references missing Workspace '{tenantName}/{workspaceName}'.");
        if (principal is null)
        {
            planning.Register(Kind, resourceName);
            return new(BootstrapResourceDisposition.Create);
        }
        var existing = await store.GetPrincipalPreferencesAsync(principal.Id, cancellationToken);
        if (existing?.DefaultTenantId is null && existing?.DefaultWorkspaceId is null)
        {
            planning.Register(Kind, resourceName);
            return new(BootstrapResourceDisposition.Create);
        }
        if (tenant is null) return new(BootstrapResourceDisposition.Conflict);
        var workspace = await store.FindWorkspaceByNameAsync(tenant.Id, workspaceName, cancellationToken);
        return existing.DefaultTenantId == tenant.Id && existing.DefaultWorkspaceId == workspace?.Id
            ? new(BootstrapResourceDisposition.Skip)
            : new(BootstrapResourceDisposition.Conflict);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var (resourceName, userName, tenantName, workspaceName) = Read(resource);
        var principal = await principals.ResolveByUserNameAsync(userName, cancellationToken)
            ?? throw new InvalidOperationException($"PrincipalDefaultContext references missing local account '{userName}'.");
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken)
            ?? throw new InvalidOperationException($"PrincipalDefaultContext references missing Tenant '{tenantName}'.");
        var workspace = await store.FindWorkspaceByNameAsync(tenant.Id, workspaceName, cancellationToken)
            ?? throw new InvalidOperationException(
                $"PrincipalDefaultContext references missing Workspace '{tenantName}/{workspaceName}'.");

        var existing = await store.GetPrincipalPreferencesAsync(principal.Id, cancellationToken);
        if (existing?.DefaultTenantId is not null || existing?.DefaultWorkspaceId is not null)
            return existing.DefaultTenantId == tenant.Id && existing.DefaultWorkspaceId == workspace.Id
                ? BootstrapResourceApplyResult.Skipped
                : BootstrapResourceApplyResult.Conflict;

        var now = timeProvider.GetUtcNow();
        await store.UpsertPrincipalPreferencesAsync(
            (existing ?? new PrincipalPreferences(principal.Id, ThemePreference.System, now)) with
            {
                DefaultTenantId = tenant.Id,
                DefaultWorkspaceId = workspace.Id,
                UpdatedAt = now
            },
            cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static (string ResourceName, string UserName, string TenantName, string WorkspaceName) Read(
        BootstrapResourceDocument resource)
    {
        var resourceName = IdentityBootstrapValidation.Name(resource.Metadata.Name, "PrincipalDefaultContext");
        var definition = resource.Definition.Deserialize<PrincipalDefaultContextBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("PrincipalDefaultContext definition is required.");
        var userName = definition.PrincipalRef.LocalAccount.Trim();
        if (userName.Length is < 3 or > 64)
            throw new InvalidOperationException("PrincipalDefaultContext definition.principalRef.localAccount must contain between 3 and 64 characters.");
        if (!string.Equals(resourceName, userName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PrincipalDefaultContext metadata.name must match definition.principalRef.localAccount.");
        return (
            resourceName,
            userName,
            IdentityBootstrapValidation.Name(definition.TenantRef.Name, "Tenant"),
            IdentityBootstrapValidation.Name(definition.WorkspaceRef.Name, "Workspace"));
    }
}
