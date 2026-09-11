using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;

namespace Agentstration.Web.Api.Runtime;

internal sealed class PrepareAgentRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{agentName}/prepare", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanWriteResources);
    public static void MapNamespaced(RouteGroupBuilder group) => group.MapPost("/{agentName}/prepare", HandleNamespacedAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanWriteResources);

    private static Task<IResult> HandleAsync(
        string agentName,
        long generation,
        AgentManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Default, agentName, generation, service, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        long generation,
        AgentManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Parse(@namespace), agentName, generation, service, cancellationToken);

    private static Task<IResult> ExecuteAsync(
        ResourceNamespace @namespace,
        string agentName,
        long generation,
        AgentManagementService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            var deployment = await service.PrepareLocalRuntimeAsync(@namespace, agentName, generation, cancellationToken);
            return Results.Ok(new PrepareAgentRuntimeResponse(
                agentName,
                generation,
                deployment.Value.Metadata.Name,
                deployment.Value.RevisionName,
                deployment.Value.OperationalState.ToString()));
        });
}
