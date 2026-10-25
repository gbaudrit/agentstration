using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeAgentAccessValidator(
    IResourceAccessPolicyService accessPolicies)
    : IRuntimeAgentAccessValidator
{
    public async Task ValidateUseAsync(
        ResolvedRuntimeAgent agent,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        if (agent.AgentScopeRef is not { } ownerScopeRef)
            throw NotFound(agent);
        if (ownerScopeRef == consumerScopeRef) return;

        var decision = await accessPolicies.EvaluateAsync(
            new ResourceAccessIdentity(
                agent.AgentId,
                ownerScopeRef,
                agent.AgentNamespace,
                "Agent",
                agent.Generation),
            consumerScopeRef,
            ResourceAccessAction.Use,
            cancellationToken);
        if (!decision.Allowed) throw NotFound(agent);
    }

    private static RuntimeAgentResolutionException NotFound(ResolvedRuntimeAgent agent) =>
        new("agent_not_found", $"Agent '{agent.AgentNamespace}/{agent.AgentName}' was not found.");
}
