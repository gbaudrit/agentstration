using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Web.Api.Models;

internal sealed class GetAgentModelEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{agentName}/model", HandleAsync);
    public static void MapNamespaced(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/api/namespaces/{namespace}/agents/{agentName}/model", HandleNamespacedAsync);

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
        CancellationToken cancellationToken) => ModelManagementHttp.ExecuteAsync(async () =>
        {
            var agent = await agents.GetAgentAsync(@namespace, agentName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(ResourceKey.Create(ResourceKinds.Agent, agentName, @namespace));
            var profileAddress = agent.Value.Definition.ModelProfile.Resolve(agent.Value.Namespace, ResourceKinds.ModelProfile);
            var profile = await profiles.GetAsync(profileAddress.Namespace, profileAddress.Name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(ResourceKey.Create(ResourceKinds.ModelProfile, profileAddress.Name, profileAddress.Namespace));
            var resolution = await profiles.ResolveAsync(profile.Value, cancellationToken);
            var mapped = ModelManagementHttp.Resolution(resolution);
            return Results.Ok(new AgentModelResponse(
                new DeclaredAgentModelResponse(new ModelProfileIdentityResponse(profile.Value.Metadata.Name, profile.Value.Metadata.Name, profile.Value.Definition.DisplayName, profile.Value.Namespace.Value)),
                new ResolvedAgentModelResponse(mapped.Provider, mapped.Model, mapped.EffectiveOptions),
                mapped.Status,
                mapped.Warnings));
        });
}
