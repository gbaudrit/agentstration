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
        return new ResolvedRuntimeAgent(
            agent.Value.Uid,
            agent.Value.Metadata.Name,
            revision.Value.AgentVersion,
            deployment.Value.Uid.ToString("N"),
            revision.Value.Metadata.Name,
            deployment.Value.RuntimeProfileName,
            deployment.Value.ModelProfileName ?? revision.Value.Definition.ModelProfileName,
            await RuntimeAgentDefinitionMapper.ToExecutableAsync(revision.Value.Definition, revision.Value.Namespace, store, cancellationToken),
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
        Memory = definition.Memory is null ? null : new ExecutableAgentMemoryConfiguration
        {
            ProfileName = definition.Memory.Profile.Name,
            ReadOwnMemory = definition.Memory.ReadOwnMemory,
            SharedScopes = definition.Memory.SharedScopes
        },
        Capabilities = definition.Capabilities,
        Handler = definition.Handler,
        DefinitionHash = definition.DefinitionHash
    };

    public static async Task<ExecutableAgentDefinition> ToExecutableAsync(
        ResolvedAgentDefinition definition,
        ResourceNamespace ownerNamespace,
        IControlPlaneStore store,
        CancellationToken cancellationToken)
    {
        ExecutableAgentMemoryConfiguration? memory = null;
        if (definition.Memory is { } configured)
        {
            var profileAddress = configured.Profile.Resolve(ownerNamespace, ResourceKinds.MemoryProfile);
            var profile = await store.GetAsync<MemoryProfileResource>(new(profileAddress.Kind, profileAddress.Name, profileAddress.Namespace), cancellationToken)
                ?? throw new RuntimeAgentResolutionException("memory_profile_not_found", $"Memory profile '{profileAddress}' was not found.");
            var providerAddress = profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ResourceKinds.MemoryProvider);
            _ = await store.GetAsync<MemoryProviderResource>(new(providerAddress.Kind, providerAddress.Name, providerAddress.Namespace), cancellationToken)
                ?? throw new RuntimeAgentResolutionException("memory_provider_not_found", $"Memory provider '{providerAddress}' was not found.");
            memory = new ExecutableAgentMemoryConfiguration
            {
                ProfileName = profile.Value.Name,
                ProviderName = providerAddress.Name,
                Namespace = providerAddress.Namespace.Value,
                ReadOwnMemory = configured.ReadOwnMemory,
                SharedScopes = configured.SharedScopes,
                MaximumRecords = profile.Value.Definition.Retrieval.MaximumRecords,
                DefaultTimeToLive = profile.Value.Definition.Retention.DefaultTimeToLive
            };
        }
        return new()
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
        Memory = memory,
        Capabilities = definition.Capabilities,
        Handler = definition.Handler,
        DefinitionHash = definition.DefinitionHash
        };
    }
}
