using Agentstration.Agents;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal sealed class GetAgentModelEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{agentName}/model", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
    public static void MapNamespaced(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/api/namespaces/{namespace}/agents/{agentName}/model", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);

    private static Task<IResult> HandleAsync(
        string agentName,
        string? scopeRef,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        IResourceScopeResolver scopes,
        CancellationToken cancellationToken) => HandleCoreAsync(ResourceNamespace.Default, agentName, scopeRef, agents, profiles, scopes, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        string? scopeRef,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        IResourceScopeResolver scopes,
        CancellationToken cancellationToken) => HandleCoreAsync(ResourceNamespace.Parse(@namespace), agentName, scopeRef, agents, profiles, scopes, cancellationToken);

    private static Task<IResult> HandleCoreAsync(
        ResourceNamespace @namespace,
        string agentName,
        string? scopeRef,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        IResourceScopeResolver scopes,
        CancellationToken cancellationToken) => ModelsApiHttp.ExecuteAsync(async () =>
        {
            ResourceScopeRef? requestedScope = string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef);
            if (requestedScope is { } exact && await scopes.ResolveAsync(exact, cancellationToken) is null)
                throw new ResourceNotFoundException(ResourceKey.Create(AgentResourceKinds.Agent, agentName, @namespace));
            var agent = await agents.GetAgentAsync(@namespace, agentName, requestedScope, cancellationToken)
                ?? throw new ResourceNotFoundException(ResourceKey.Create(AgentResourceKinds.Agent, agentName, @namespace));
            var agentScopeRef = agent.Value.ScopeRef
                ?? throw new InvalidOperationException($"Agent '{agent.Value.Address}' has no ownership scope.");
            var profilePin = await profiles.ResolveReferenceAsync(agent.Value.Definition.ModelProfile,
                agent.Value.Namespace, agentScopeRef, cancellationToken);
            var profile = await profiles.GetExactAsync(profilePin.ScopeRef, profilePin.Namespace,
                    profilePin.Name, cancellationToken)
                ?? throw new ResourceNotFoundException(ResourceKey.Create(ModelResourceKinds.ModelProfile,
                    profilePin.Name, profilePin.Namespace));
            var resolution = await profiles.ResolveAsync(profile.Value, cancellationToken);
            var mapped = ModelsApiHttp.Resolution(resolution);
            return Results.Ok(new AgentModelResponse(
                new DeclaredAgentModelResponse(new ModelProfileIdentityResponse(profile.Value.Metadata.Name, profile.Value.Metadata.Name, profile.Value.Definition.DisplayName, profile.Value.Namespace.Value, profile.Value.ScopeRef)),
                new ResolvedAgentModelResponse(mapped.Provider, mapped.Model, mapped.EffectiveOptions),
                mapped.Status,
                mapped.Warnings));
        });
}
