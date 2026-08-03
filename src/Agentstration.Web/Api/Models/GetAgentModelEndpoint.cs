using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Models;

internal sealed class GetAgentModelEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{agentName}/model", HandleAsync);

    private static Task<IResult> HandleAsync(
        string agentName,
        string? resourceGroup,
        AgentManagementService agents,
        ModelProfileManagementService profiles,
        CancellationToken cancellationToken) => ModelManagementHttp.ExecuteAsync(async () =>
        {
            var groupName = ModelManagementHttp.ResourceGroup(resourceGroup);
            var agentId = ResourceIdentifier.Create(groupName, AgentstrationProviderNamespaces.Agents, "agents", agentName).Value;
            var agent = await agents.GetAgentAsync(agentId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(agentId);
            var profileId = agent.Value.Properties.ModelProfile.ResourceId;
            var profileIdentifier = ResourceIdentifier.Parse(profileId);
            var profile = await profiles.GetAsync(profileIdentifier.ResourceGroup, profileIdentifier.Name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(profileId);
            var resolution = await profiles.ResolveAsync(profile.Value, cancellationToken);
            var mapped = ModelManagementHttp.Resolution(resolution);
            return Results.Ok(new AgentModelResponse(
                new DeclaredAgentModelResponse(new ModelProfileIdentityResponse(profile.Value.Id, profile.Value.Name, profile.Value.Properties.DisplayName)),
                new ResolvedAgentModelResponse(mapped.Provider, mapped.Model, mapped.EffectiveOptions),
                mapped.Status,
                mapped.Warnings));
        });
}
