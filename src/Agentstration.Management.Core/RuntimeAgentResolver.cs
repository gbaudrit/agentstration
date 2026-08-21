using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Management.Core;

public sealed class ControlPlaneRuntimeAgentResolver(
    IControlPlaneStore store,
    IAgentResourceQueries queries) : IRuntimeAgentResolver
{
    public async Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, CancellationToken cancellationToken)
        => await ResolveLatestAsync(resourceId, ResourceNamespace.Default, cancellationToken);

    public async Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var agent = await store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, resourceId, @namespace), cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_not_found", $"Agent '{@namespace}/{resourceId}' was not found.");
        return await ResolveAsync(new RuntimeAgentReference(resourceId, agent.Value.Generation) { Namespace = @namespace }, cancellationToken);
    }

    public async Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken)
    {
        var agent = await store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, reference.ResourceId, reference.Namespace), cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_not_found", $"Agent '{reference.Namespace}/{reference.ResourceId}' was not found.");
        var revision = await queries.FindRevisionAsync(agent.Value.Uid, reference.Version, cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_version_not_found", $"Agent version '{reference.Version}' does not exist.");
        var deployment = await queries.FindDeploymentByRevisionAsync(reference.Namespace, revision.Value.Metadata.Name, cancellationToken)
            ?? throw new RuntimeAgentResolutionException("deployment_not_found", $"Agent generation '{reference.Version}' has no deployment.");
        var ready = deployment.Value.DesiredState == DesiredAgentState.Running
            && deployment.Value.OperationalState == OperationalState.Ready;
        var modelProfileNamespace = deployment.Value.ModelProfileNamespace
            ?? revision.Value.Definition.ModelProfileNamespace
            ?? (agent.Value.Generation == reference.Version
                ? agent.Value.Definition.ModelProfile.Resolve(agent.Value.Namespace, ResourceKinds.ModelProfile).Namespace
                : ResourceNamespace.Default);
        return new ResolvedRuntimeAgent(
            agent.Value.Uid,
            agent.Value.Metadata.Name,
            revision.Value.AgentVersion,
            deployment.Value.Uid.ToString("N"),
            revision.Value.Metadata.Name,
            deployment.Value.RuntimeProfileName,
            deployment.Value.ModelProfileName ?? revision.Value.Definition.ModelProfileName,
            RuntimeAgentDefinitionMapper.ToExecutable(revision.Value.Definition, modelProfileNamespace),
            ready,
            ready ? "Ready" : deployment.Value.OperationalState.ToString(),
            ready ? null : deployment.Value.LastError ?? $"Deployment is {deployment.Value.OperationalState}.")
        {
            RuntimeProfileNamespace = deployment.Value.RuntimeProfileNamespace,
            ModelProfileNamespace = modelProfileNamespace
        };
    }
}

public static class RuntimeAgentDefinitionMapper
{
    public static ExecutableAgentDefinition ToExecutable(ResolvedAgentDefinition definition, ResourceNamespace? modelProfileNamespace = null) => new()
    {
        AgentId = definition.AgentId,
        AgentKey = definition.AgentKey,
        DisplayName = definition.DisplayName,
        Description = definition.Description,
        AgentVersion = definition.AgentVersion,
        EffectiveInstructions = definition.EffectiveInstructions,
        ModelProfileName = definition.ModelProfileName,
        ModelProfileNamespace = modelProfileNamespace ?? definition.ModelProfileNamespace ?? ResourceNamespace.Default,
        RuntimeProfileName = definition.RuntimeProfileName,
        RuntimeProfileNamespace = definition.RuntimeProfileNamespace,
        EffectiveToolNames = definition.EffectiveToolNames,
        MiddlewareIds = definition.MiddlewareIds,
        ContextProviderIds = definition.ContextProviderIds,
        Capabilities = definition.Capabilities,
        Handler = definition.Handler,
        DefinitionHash = definition.DefinitionHash
    };
}
