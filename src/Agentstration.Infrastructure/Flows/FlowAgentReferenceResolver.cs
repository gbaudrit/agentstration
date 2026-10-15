using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowAgentReferenceResolver(
    IResourceReferenceResolver references,
    IResourceAccessPolicyService accessPolicies,
    AgentManagementService agents) : IFlowAgentReferenceResolver
{
    public async Task<FlowTargetReference> ResolveAsync(ResourceScopeRef ownerScopeRef,
        ResourceNamespace ownerNamespace, FlowTargetReference reference, CancellationToken cancellationToken)
    {
        var resourceReference = new ResourceReference(reference.Id, reference.ScopeRef, reference.Namespace);
        StoredResource<AgentResource>? stored;
        try
        {
            stored = await references.ResolveAsync<AgentResource>(resourceReference, ownerNamespace,
                AgentResourceKinds.Agent, ownerScopeRef, cancellationToken);
        }
        catch (ResourceReferenceAmbiguousException exception)
        {
            throw new FlowValidationException("agent_reference_ambiguous", exception.Message);
        }
        catch (ResourceReferenceOutsideScopeException exception)
        {
            throw new FlowValidationException("agent_reference_outside_scope", exception.Message);
        }
        if (stored is null)
            throw new FlowValidationException("agent_resource_not_found", $"Agent '{reference.Id}' was not found.");
        var agentScopeRef = stored.Value.ScopeRef
            ?? throw new FlowValidationException("agent_scope_missing", $"Agent '{stored.Value.Address}' has no ownership scope.");
        if (agentScopeRef != ownerScopeRef)
        {
            var access = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(stored.Value.Uid,
                agentScopeRef, stored.Value.Namespace, AgentResourceKinds.Agent, stored.Value.Generation),
                ownerScopeRef, ResourceAccessAction.Use, cancellationToken);
            if (!access.Allowed)
                throw new FlowValidationException("agent_access_denied",
                    $"Agent '{stored.Value.Address}' does not authorize use from '{ownerScopeRef}' ({access.ReasonCode}).");
        }
        var generation = stored.Value.Generation;
        if (!string.IsNullOrWhiteSpace(reference.Version)
            && (!long.TryParse(reference.Version, out generation) || generation < 1))
            throw new FlowValidationException("agent_version_invalid",
                $"Agent version '{reference.Version}' is not a positive generation number.");
        var revision = await agents.EnsureExecutionRevisionAsync(stored.Value.Namespace, stored.Value.Name,
            agentScopeRef, generation, cancellationToken);
        return reference with
        {
            Namespace = stored.Value.Namespace,
            ScopeRef = agentScopeRef,
            Version = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceUid = stored.Value.Uid,
            RevisionUid = revision.Value.Uid,
            DefinitionHash = revision.Value.DefinitionHash
        };
    }
}
