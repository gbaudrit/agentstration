using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;

namespace Agentstration.Web.Api.Runtime;

internal sealed class PrepareAgentRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{agentName}/prepare", HandleAsync);

    private static Task<IResult> HandleAsync(
        string agentName,
        string? resourceGroup,
        long generation,
        AgentManagementService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            var groupName = string.IsNullOrWhiteSpace(resourceGroup) ? "default" : resourceGroup;
            var agentId = ResourceIdentifier.Create(groupName, AgentstrationProviderNamespaces.Agents, "agents", agentName).Value;
            var deployment = await service.PrepareLocalRuntimeAsync(agentId, generation, cancellationToken);
            return Results.Ok(new PrepareAgentRuntimeResponse(
                agentId,
                generation,
                deployment.Value.Id,
                deployment.Value.RevisionId,
                deployment.Value.OperationalState.ToString()));
        });
}
