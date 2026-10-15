using Agentstration.Agents;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Agents;

public sealed class ResourceRuntimeAgentResolver(
    IResourceStore store,
    IAgentResourceQueries queries) : IRuntimeAgentResolver
{
    public async Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, CancellationToken cancellationToken)
        => await ResolveLatestAsync(resourceId, ResourceNamespace.Default, cancellationToken);

    public async Task<ResolvedRuntimeAgent> ResolveLatestAsync(string resourceId, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var agent = await store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, resourceId, @namespace), cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_not_found", $"Agent '{@namespace}/{resourceId}' was not found.");
        return await ResolveAsync(new RuntimeAgentReference(resourceId, agent.Value.Generation) { Namespace = @namespace }, cancellationToken);
    }

    public async Task<ResolvedRuntimeAgent> ResolveAsync(RuntimeAgentReference reference, CancellationToken cancellationToken)
    {
        var agent = reference.ScopeRef is { } scopeRef
            ? await store.GetExactAsync<AgentResource>(ScopedResourceAddress.Create(scopeRef, reference.Namespace,
                AgentResourceKinds.Agent, reference.ResourceId), cancellationToken)
            : await store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, reference.ResourceId, reference.Namespace), cancellationToken);
        agent = agent
            ?? throw new RuntimeAgentResolutionException("agent_not_found", $"Agent '{reference.Namespace}/{reference.ResourceId}' was not found.");
        if (reference.ResourceUid is { } resourceUid && agent.Value.Uid != resourceUid)
            throw new RuntimeAgentResolutionException("agent_reference_stale", "The Agent no longer matches the exact pinned identity.");
        var revision = await queries.FindRevisionAsync(agent.Value.Uid, reference.Version, cancellationToken)
            ?? throw new RuntimeAgentResolutionException("agent_version_not_found", $"Agent version '{reference.Version}' does not exist.");
        if (revision.Value.ScopeRef != agent.Value.ScopeRef || revision.Value.AgentUid != agent.Value.Uid)
            throw new RuntimeAgentResolutionException("agent_revision_scope_mismatch", "The Agent revision does not belong to the exact Agent ownership scope.");
        if (reference.RevisionUid is { } revisionUid && revision.Value.Uid != revisionUid
            || reference.DefinitionHash is { } definitionHash
                && !string.Equals(revision.Value.DefinitionHash, definitionHash, StringComparison.Ordinal))
            throw new RuntimeAgentResolutionException("agent_reference_stale", "The Agent revision no longer matches the exact pinned identity.");
        await ValidatePinnedDependenciesAsync(revision.Value.Definition, cancellationToken);
        var modelProfileNamespace = revision.Value.Definition.ModelProfileNamespace
            ?? (agent.Value.Generation == reference.Version
                ? agent.Value.Definition.ModelProfile.Resolve(agent.Value.Namespace, ModelResourceKinds.ModelProfile).Namespace
                : ResourceNamespace.Default);
        return new ResolvedRuntimeAgent(
            agent.Value.Uid,
            agent.Value.Metadata.Name,
            revision.Value.AgentVersion,
            string.Empty,
            revision.Value.Metadata.Name,
            revision.Value.Definition.RuntimeProfileName,
            revision.Value.Definition.ModelProfileName,
            RuntimeAgentDefinitionMapper.ToExecutable(revision.Value.Definition, modelProfileNamespace),
            true,
            "Ready",
            null)
        {
            AgentNamespace = reference.Namespace,
            AgentScopeRef = agent.Value.ScopeRef,
            RevisionUid = revision.Value.Uid,
            RuntimeProfileNamespace = revision.Value.Definition.RuntimeProfileNamespace,
            ModelProfileNamespace = modelProfileNamespace
        };
    }

    private async Task ValidatePinnedDependenciesAsync(ResolvedAgentDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.ModelProfilePin is { } model)
            await ValidatePinAsync<ModelProfileResource>(model, cancellationToken);
        if (definition.RuntimeProfilePin is { } runtime)
            await ValidatePinAsync<RuntimeProfileResource>(runtime, cancellationToken);
        foreach (var tool in definition.ToolPins)
            await ValidatePinAsync<ToolResource>(tool, cancellationToken);
        foreach (var assignment in definition.ToolSetAssignments)
        {
            var current = (await store.ListAllAsync<ToolSetVersionResource>(ToolResourceKinds.ToolSetVersion, cancellationToken))
                .SingleOrDefault(value => value.Value.Uid == assignment.ToolSetUid);
            if (current is null
                || current.Value.ScopeRef != assignment.ToolSetScopeRef
                || current.Value.Generation != assignment.ToolSetGeneration
                || !string.Equals(current.Value.DefinitionHash, assignment.DefinitionHash, StringComparison.Ordinal))
                throw new RuntimeAgentResolutionException("agent_dependency_stale",
                    $"Pinned ToolSet '{assignment.ToolSetNamespace}/{assignment.ToolSetName}:{assignment.Version}' is stale.");
        }
    }

    private async Task ValidatePinAsync<T>(ResolvedResourceDependency pin, CancellationToken cancellationToken)
        where T : Resource
    {
        var current = await store.GetExactAsync<T>(ScopedResourceAddress.Create(
            pin.ScopeRef, pin.Namespace, pin.Kind, pin.Name), cancellationToken);
        if (current is null || current.Value.Uid != pin.Uid || current.Value.Generation != pin.Generation)
            throw new RuntimeAgentResolutionException("agent_dependency_stale",
                $"Pinned {pin.Kind} '{pin.Namespace}/{pin.Name}' is stale.");
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
        ToolReferences = definition.ToolPins.Select(value => new RuntimeToolReference(
            ToolResourceIdentity.CatalogId(value.Namespace, value.Name), value.Namespace, value.ScopeRef,
            value.Uid, value.Generation)).ToArray(),
        MiddlewareIds = definition.MiddlewareIds,
        ContextProviderIds = definition.ContextProviderIds,
        Capabilities = definition.Capabilities,
        Handler = definition.Handler,
        DefinitionHash = definition.DefinitionHash
    };
}
