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
        string? resourceGroup,
        long generation,
        RuntimeRunService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            var groupName = string.IsNullOrWhiteSpace(resourceGroup) ? "default" : resourceGroup;
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            var id = ResourceIdentifier.Create(groupName, AgentstrationProviderNamespaces.Agents, "agents", agentName).Value;
            var readiness = await service.GetReadinessAsync(id, generation, cancellationToken);
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
