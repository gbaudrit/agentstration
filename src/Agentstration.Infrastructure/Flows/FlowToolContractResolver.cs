using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Flows.Application;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowToolContractResolver(IResourceReferenceResolver references,
    ICurrentRequestContext current, IRequestContextScopeFactory requestScopes) : IFlowToolContractResolver
{
    public async Task<FlowToolReference> ResolveAsync(ResourceScopeRef ownerScopeRef, ResourceNamespace ownerNamespace,
        FlowToolReference reference, CancellationToken cancellationToken)
    {
        // Match the exact reference-reading scope used by governed Flow Tool execution.
        // ResolveAsync still checks the referencing Flow's ancestor chain before any read.
        using var exact = current.AccessMode == ControlPlaneAccessMode.System ? null : reference.ScopeRef?.Kind switch
        {
            ResourceScopeKind.Instance => requestScopes.PushInstance(current.Current),
            ResourceScopeKind.Tenant when reference.ScopeRef.Value.TargetId == current.Current.TenantId => requestScopes.PushTenantAdministration(current.Current),
            _ => null
        };
        StoredResource<ToolResource>? stored;
        try
        {
            stored = await references.ResolveAsync<ToolResource>(new(reference.ResourceId, reference.ScopeRef, reference.Namespace),
            ownerNamespace, ToolResourceKinds.Tool, ownerScopeRef, cancellationToken)
            ?? throw new FlowValidationException("tool_not_found", "The Flow Tool was not found.");
        }
        catch (ResourceReferenceAmbiguousException exception) { throw new FlowValidationException("tool_reference_ambiguous", exception.Message); }
        catch (ResourceReferenceOutsideScopeException exception) { throw new FlowValidationException("tool_reference_outside_scope", exception.Message); }
        var tool = stored.Value;
        var scope = tool.ScopeRef ?? ownerScopeRef;
        if (tool.Definition.Schema is not { Input: { } input, Output: { } output })
            throw new FlowValidationException("tool_contract_required", $"Tool '{tool.Address}' called directly by a Flow requires explicit input and output schemas.");
        var providerReference = tool.Definition.Provider
            ?? throw new FlowValidationException("tool_provider_not_found", "The Flow Tool has no provider mapping.");
        var provider = await references.ResolveAsync<ToolProviderResource>(providerReference, tool.Namespace,
            ToolResourceKinds.ToolProvider, scope, cancellationToken)
            ?? throw new FlowValidationException("tool_provider_not_found", "The Flow Tool provider was not found.");
        output = ToolInvocationContracts.OutputSchema(output, provider.Value.Definition)!.Value;
        _ = DataContracts.Declared(input, $"tool:{tool.Address}/input");
        _ = DataContracts.Declared(output, $"tool:{tool.Address}/output");
        return reference with { Namespace = tool.Namespace, ScopeRef = scope, InputSchema = input.Clone(), OutputSchema = output.Clone() };
    }
}
