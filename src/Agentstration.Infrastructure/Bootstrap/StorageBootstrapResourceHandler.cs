using Agentstration.ResourceManagement.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class StorageBootstrapResourceHandler(
    StorageManagementService service) : IBootstrapResourceHandler
{
    public string Kind => StorageResourceKinds.Storage;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<StorageResource>(resource);
        var scopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scopeRef };
        if (await service.GetExactAsync(scopeRef, value.Namespace, value.Name, cancellationToken) is { } existing)
        {
            EnsureSameDependencies(existing.Value, value);
            return new(BootstrapResourceDisposition.Skip);
        }
        await service.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<StorageResource>(resource);
        var scopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scopeRef };
        if (await service.GetExactAsync(scopeRef, value.Namespace, value.Name, cancellationToken) is { } existing)
        {
            EnsureSameDependencies(existing.Value, value);
            return BootstrapResourceApplyResult.Skipped;
        }
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static void EnsureSameDependencies(StorageResource existing, StorageResource desired)
    {
        if (existing.Definition.Type != desired.Definition.Type || existing.Definition.ConnectedService != desired.Definition.ConnectedService
            || existing.Definition.ConnectedServiceType != desired.Definition.ConnectedServiceType)
            throw new InvalidOperationException("bootstrap_dependency_revision_changed");
    }
}
