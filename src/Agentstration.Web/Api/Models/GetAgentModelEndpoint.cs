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
            var agent = await agents.GetAgentAsync(agentName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, agentName));
            var profileName = agent.Value.Definition.ModelProfile.Name;
            var profile = await profiles.GetAsync(profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName));
            var resolution = await profiles.ResolveAsync(profile.Value, cancellationToken);
            var mapped = ModelManagementHttp.Resolution(resolution);
            return Results.Ok(new AgentModelResponse(
                new DeclaredAgentModelResponse(new ModelProfileIdentityResponse(profile.Value.Id, profile.Value.Name, profile.Value.Properties.DisplayName)),
                new ResolvedAgentModelResponse(mapped.Provider, mapped.Model, mapped.EffectiveOptions),
                mapped.Status,
                mapped.Warnings));
        });
}
