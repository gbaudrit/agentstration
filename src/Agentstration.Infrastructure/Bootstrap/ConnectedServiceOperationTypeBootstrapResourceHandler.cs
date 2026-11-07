using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ConnectedServiceOperationTypeBootstrapResourceHandler(
    ConnectedServiceOperationTypeManagementService service) : IBootstrapResourceHandler
{
    public string Kind => ConnectedServiceResourceKinds.ConnectedServiceOperationType;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ConnectedServiceOperationTypeResource>(resource);
        var scopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scopeRef };
        if (await service.GetExactAsync(scopeRef, value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        await service.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ConnectedServiceOperationTypeResource>(resource);
        var scopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        value = value with { ScopeRef = scopeRef };
        if (await service.GetExactAsync(scopeRef, value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}
