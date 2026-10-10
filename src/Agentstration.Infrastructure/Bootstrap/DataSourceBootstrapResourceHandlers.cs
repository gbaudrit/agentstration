using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class DataSourceProfileBootstrapResourceHandler(DataSourceProfileService service)
    : IBootstrapResourceHandler
{
    public string Kind => DataSourceResourceKinds.DataSourceProfile;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DataSourceProfileResource>(resource);
        var scope = value.ScopeRef ?? TargetScope(operation);
        value = value with { ScopeRef = scope };
        if (await service.GetAsync(value.Namespace, value.Name, scope, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        DataSourceProfileService.Validate(value);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DataSourceProfileResource>(resource);
        var scope = value.ScopeRef ?? TargetScope(operation);
        value = value with { ScopeRef = scope };
        if (await service.GetAsync(value.Namespace, value.Name, scope, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        _ = await service.PublishAsync(value.Namespace, value.Name, scope,
            new(value.Definition.Version, true), cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    internal static ResourceScopeRef TargetScope(BootstrapResourceOperationContext operation) => operation.ProfileScope switch
    {
        BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
        BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(operation.Target?.TenantId
            ?? throw new InvalidOperationException("A Tenant bootstrap profile requires an explicit Tenant target.")),
        BootstrapProfileScope.Workspace => ResourceScopeRef.Workspace(operation.Target?.WorkspaceId
            ?? throw new InvalidOperationException("A Workspace bootstrap profile requires an explicit Workspace target.")),
        _ => throw new InvalidOperationException($"Unsupported bootstrap scope '{operation.ProfileScope}'.")
    };
}

public sealed class DataSourceBootstrapResourceHandler(
    DataSourceManagementService service,
    DataSourceProfileService profiles) : IBootstrapResourceHandler
{
    public string Kind => DataSourceResourceKinds.DataSource;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DataSourceResource>(resource);
        var scope = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scope };
        if (await service.GetAsync(value.Namespace, value.Name, scope, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        DataSourceManagementService.Validate(value);
        var reference = value.Definition.Profile;
        var profileNamespace = reference.Namespace ?? value.Namespace;
        var planned = WorkspaceBootstrapResource.IsAvailable(planning,
            DataSourceResourceKinds.DataSourceProfile, reference.Name, profileNamespace);
        if (!planned && await profiles.GetAsync(profileNamespace, reference.Name, reference.ScopeRef, cancellationToken) is null)
            throw new InvalidOperationException(
                $"Referenced DataSourceProfile '{profileNamespace}/{reference.Name}' does not exist and was not planned earlier.");
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DataSourceResource>(resource);
        var scope = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scope };
        if (await service.GetAsync(value.Namespace, value.Name, scope, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}
