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
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        CancellationToken cancellationToken) => HandleCoreAsync(ResourceNamespace.Default, agentName, agents, profiles, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        CancellationToken cancellationToken) => HandleCoreAsync(ResourceNamespace.Parse(@namespace), agentName, agents, profiles, cancellationToken);

    private static Task<IResult> HandleCoreAsync(
        ResourceNamespace @namespace,
        string agentName,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        CancellationToken cancellationToken) => ModelsApiHttp.ExecuteAsync(async () =>
        {
            var agent = await agents.GetAgentAsync(@namespace, agentName, cancellationToken)
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
                new DeclaredAgentModelResponse(new ModelProfileIdentityResponse(profile.Value.Metadata.Name, profile.Value.Metadata.Name, profile.Value.Definition.DisplayName, profile.Value.Namespace.Value)),
                new ResolvedAgentModelResponse(mapped.Provider, mapped.Model, mapped.EffectiveOptions),
                mapped.Status,
                mapped.Warnings));
        });
}
