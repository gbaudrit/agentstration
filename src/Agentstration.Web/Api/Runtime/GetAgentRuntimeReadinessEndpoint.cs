using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class GetAgentRuntimeReadinessEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{agentName}/readiness", HandleAsync);

    private static Task<IResult> HandleAsync(
        string agentName,
        long generation,
        RuntimeRunService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            var readiness = await service.GetReadinessAsync(agentName, generation, cancellationToken);
            return Results.Ok(new AgentRuntimeReadinessResponse(
                readiness.AgentResourceId,
                readiness.Generation,
                readiness.Ready,
                readiness.State,
                readiness.DeploymentId,
                readiness.RevisionId,
                readiness.Error,
                readiness.RuntimeProfileId,
                readiness.ModelProfileId));
        });
}
