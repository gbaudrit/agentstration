using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Management.Core;

public sealed class ControlPlaneRuntimeAgentResolver(
    IControlPlaneStore store,
    IAgentResourceQueries queries) : IRuntimeAgentResolver
{
    public async Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken)
    {
        var agent = await store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, reference.ResourceId), cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_not_found", $"Agent '{reference.ResourceId}' was not found.");
        var revision = await queries.FindRevisionAsync(agent.Value.Uid, reference.Version, cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_version_not_found", $"Agent version '{reference.Version}' does not exist.");
        var deployment = await queries.FindDeploymentByRevisionAsync(revision.Value.Metadata.Name, cancellationToken)
            ?? throw new RuntimeAgentResolutionException("deployment_not_found", $"Agent generation '{reference.Version}' has no deployment.");
        var ready = deployment.Value.DesiredState == DesiredAgentState.Running
            && deployment.Value.OperationalState == OperationalState.Ready;
        return new ResolvedRuntimeAgent(
            agent.Value.Uid,
            agent.Value.Metadata.Name,
            revision.Value.AgentVersion,
            deployment.Value.Uid.ToString("N"),
            revision.Value.Metadata.Name,
            deployment.Value.RuntimeProfileName,
            deployment.Value.ModelProfileName ?? revision.Value.Definition.ModelProfileName,
            RuntimeAgentDefinitionMapper.ToExecutable(revision.Value.Definition),
            ready,
            ready ? "Ready" : deployment.Value.OperationalState.ToString(),
            ready ? null : deployment.Value.LastError ?? $"Deployment is {deployment.Value.OperationalState}.");
    }
}

public static class RuntimeAgentDefinitionMapper
{
    public static ExecutableAgentDefinition ToExecutable(ResolvedAgentDefinition definition) => new()
    {
        AgentId = definition.AgentId,
        AgentKey = definition.AgentKey,
        DisplayName = definition.DisplayName,
        Description = definition.Description,
        AgentVersion = definition.AgentVersion,
        EffectiveInstructions = definition.EffectiveInstructions,
        ModelProfileName = definition.ModelProfileName,
        RuntimeProfileName = definition.RuntimeProfileName,
        EffectiveToolNames = definition.EffectiveToolNames,
        MiddlewareIds = definition.MiddlewareIds,
        ContextProviderIds = definition.ContextProviderIds,
        Capabilities = definition.Capabilities,
        Handler = definition.Handler,
        DefinitionHash = definition.DefinitionHash
    };
}
