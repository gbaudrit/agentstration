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
            var deployment = await service.PrepareLocalRuntimeAsync(agentName, generation, cancellationToken);
            return Results.Ok(new PrepareAgentRuntimeResponse(
                agentName,
                generation,
                deployment.Value.Metadata.Name,
                deployment.Value.RevisionName,
                deployment.Value.OperationalState.ToString()));
        });
}
