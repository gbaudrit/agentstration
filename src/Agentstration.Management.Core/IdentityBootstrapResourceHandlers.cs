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

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        CancellationToken cancellationToken)
    {
        var name = IdentityBootstrapValidation.Name(resource.Metadata.Name, "Tenant");
        var definition = resource.Definition.Deserialize<TenantBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("Tenant definition is required.");
        var displayName = IdentityBootstrapValidation.DisplayName(definition.DisplayName, "Tenant");
        if (await store.FindTenantByNameAsync(name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;

        await store.AddTenantAsync(
            new Tenant(Guid.NewGuid(), name, displayName, TenantStatus.Active, timeProvider.GetUtcNow()),
            cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class WorkspaceBootstrapResourceHandler(
    IIdentityStore store,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.Workspace;

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        CancellationToken cancellationToken)
    {
        var name = IdentityBootstrapValidation.Name(resource.Metadata.Name, "Workspace");
        var definition = resource.Definition.Deserialize<WorkspaceBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("Workspace definition is required.");
        var displayName = IdentityBootstrapValidation.DisplayName(definition.DisplayName, "Workspace");
        var tenantName = IdentityBootstrapValidation.Name(definition.TenantRef.Name, "Tenant");
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace '{name}' references missing Tenant '{tenantName}'.");
        if (await store.FindWorkspaceByNameAsync(tenant.Id, name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;

        await store.AddWorkspaceAsync(
            new Workspace(Guid.NewGuid(), tenant.Id, name, displayName, WorkspaceStatus.Active, timeProvider.GetUtcNow()),
            cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class PrincipalDefaultContextBootstrapResourceHandler(
    IIdentityStore store,
    ILocalAccountPrincipalResolver principals,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.PrincipalDefaultContext;

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        CancellationToken cancellationToken)
    {
        var resourceName = IdentityBootstrapValidation.Name(resource.Metadata.Name, "PrincipalDefaultContext");
        var definition = resource.Definition.Deserialize<PrincipalDefaultContextBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("PrincipalDefaultContext definition is required.");
        var userName = definition.PrincipalRef.LocalAccount.Trim();
        if (userName.Length is < 3 or > 64)
            throw new InvalidOperationException("PrincipalDefaultContext definition.principalRef.localAccount must contain between 3 and 64 characters.");
        if (!string.Equals(resourceName, userName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PrincipalDefaultContext metadata.name must match definition.principalRef.localAccount.");
        var principal = await principals.ResolveByUserNameAsync(userName, cancellationToken)
            ?? throw new InvalidOperationException($"PrincipalDefaultContext references missing local account '{userName}'.");
        var tenantName = IdentityBootstrapValidation.Name(definition.TenantRef.Name, "Tenant");
        var tenant = await store.FindTenantByNameAsync(tenantName, cancellationToken)
            ?? throw new InvalidOperationException($"PrincipalDefaultContext references missing Tenant '{tenantName}'.");
        var workspaceName = IdentityBootstrapValidation.Name(definition.WorkspaceRef.Name, "Workspace");
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
}
