using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class FunctionKindBootstrapResourceHandler(TransformationFunctionService service) : IBootstrapResourceHandler
{
    public string Kind => FunctionResourceKinds.FunctionKind;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope scope) => true;
    private static FunctionKindResource Parse(BootstrapResourceDocument document, BootstrapResourceOperationContext operation)
    {
        var value = WorkspaceBootstrapResource.Parse<FunctionKindResource>(document);
        return value with { ScopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
    }
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken ct)
    {
        var value = Parse(document, operation);
        if (await service.GetKindAsync(value.ScopeRef!.Value, value.Namespace, value.Name, ct) is not null) return new(BootstrapResourceDisposition.Skip);
        await service.ValidateKindAsync(value, ct);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, document);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, CancellationToken ct)
    {
        var value = Parse(document, operation);
        if (await service.GetKindAsync(value.ScopeRef!.Value, value.Namespace, value.Name, ct) is not null) return BootstrapResourceApplyResult.Skipped;
        await service.PutKindAsync(value, null, true, ct);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class TransformationFunctionBootstrapResourceHandler(TransformationFunctionService service) : IBootstrapResourceHandler
{
    public string Kind => FunctionResourceKinds.TransformationFunction;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope scope) => true;
    private static TransformationFunctionResource Parse(BootstrapResourceDocument document, BootstrapResourceOperationContext operation)
    {
        var value = WorkspaceBootstrapResource.Parse<TransformationFunctionResource>(document);
        return value with { ScopeRef = value.ScopeRef ?? DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
    }
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken ct)
    {
        var value = Parse(document, operation);
        if (await service.GetAsync(value.ScopeRef!.Value, value.Namespace, value.Name, ct) is not null) return new(BootstrapResourceDisposition.Skip);
        if (value.Definition?.Versions is null || value.Definition.Versions.Any(version => version is null)
            || value.Definition.Versions.Count is < 1 or > 64 || value.Definition.Versions.Select(v => v.Version).Distinct(StringComparer.Ordinal).Count() != value.Definition.Versions.Count)
            throw new Agentstration.Functions.Abstractions.TransformationFunctionException("function_versions_invalid", "Function versions must be bounded and unique.");
        foreach (var version in value.Definition.Versions)
        {
            FunctionKindResource? planned = null;
            if (await service.GetKindAsync(version.FunctionKind.ScopeRef ?? value.ScopeRef.Value,
                version.FunctionKind.Namespace ?? value.Namespace, version.FunctionKind.Name, ct) is null
                && planning.TryGetDocument(FunctionResourceKinds.FunctionKind, version.FunctionKind.Name,
                    WorkspaceBootstrapResource.PlanningParent(version.FunctionKind.Namespace ?? value.Namespace), out var kindDocument))
            {
                planned = WorkspaceBootstrapResource.Parse<FunctionKindResource>(kindDocument);
                planned = planned with { ScopeRef = planned.ScopeRef ?? value.ScopeRef };
            }
            await service.ValidateAsync(value with { Definition = value.Definition with { Versions = [version] } }, ct, planned);
        }
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, document);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, CancellationToken ct)
    {
        var value = Parse(document, operation);
        if (await service.GetAsync(value.ScopeRef!.Value, value.Namespace, value.Name, ct) is not null) return BootstrapResourceApplyResult.Skipped;
        await service.PutAsync(value, null, true, ct);
        return BootstrapResourceApplyResult.Created;
    }
}
