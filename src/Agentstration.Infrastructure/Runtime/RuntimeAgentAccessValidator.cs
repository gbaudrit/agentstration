using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeAgentAccessValidator(
    IResourceAccessPolicyService accessPolicies,
    IIdentityStore identities)
    : IRuntimeAgentAccessValidator
{
    public async Task ValidateUseAsync(
        ResolvedRuntimeAgent agent,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        if (agent.AgentScopeRef is not { } ownerScopeRef)
            throw NotFound(agent);
        var accessScopeRef = await ResolveAccessScopeAsync(consumerScopeRef, cancellationToken);
        if (ownerScopeRef == accessScopeRef) return;

        var decision = await accessPolicies.EvaluateAsync(
            new ResourceAccessIdentity(
                agent.AgentId,
                ownerScopeRef,
                agent.AgentNamespace,
                "Agent",
                agent.Generation),
            accessScopeRef,
            ResourceAccessAction.Use,
            cancellationToken);
        if (!decision.Allowed) throw NotFound(agent);
    }

    private async Task<ResourceScopeRef> ResolveAccessScopeAsync(
        ResourceScopeRef executionScopeRef,
        CancellationToken cancellationToken)
    {
        if (executionScopeRef.Kind != ResourceScopeKind.Workspace) return executionScopeRef;
        if (executionScopeRef.TargetId == OperationalTopologyIdentities.InstanceOperationsWorkspaceId)
            return ResourceScopeRef.Instance;
        var workspace = await identities.GetWorkspaceAsync(executionScopeRef.TargetId!.Value, cancellationToken);
        return MapOperationalScope(executionScopeRef, workspace);
    }

    internal static ResourceScopeRef MapOperationalScope(ResourceScopeRef executionScopeRef, Workspace? workspace)
    {
        if (executionScopeRef.Kind == ResourceScopeKind.Workspace
            && executionScopeRef.TargetId == OperationalTopologyIdentities.InstanceOperationsWorkspaceId)
            return ResourceScopeRef.Instance;
        if (workspace is not null
            && !OperationalTopologyIdentities.IsSystemTenant(workspace.TenantId)
            && workspace.Id == OperationalTopologyIdentities.TenantOperationsWorkspaceId(workspace.TenantId))
            return ResourceScopeRef.Tenant(workspace.TenantId);
        return executionScopeRef;
    }

    private static RuntimeAgentResolutionException NotFound(ResolvedRuntimeAgent agent) =>
        new("agent_not_found", $"Agent '{agent.AgentNamespace}/{agent.AgentName}' was not found.");
}
