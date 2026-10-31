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
        string? scopeRef,
        IAgentRuntimePreparationService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Default, agentName, scopeRef, generation, service, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        long generation,
        string? scopeRef,
        IAgentRuntimePreparationService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Parse(@namespace), agentName, scopeRef, generation, service, cancellationToken);

    private static Task<IResult> ExecuteAsync(
        ResourceNamespace @namespace,
        string agentName,
        string? scopeRef,
        long generation,
        IAgentRuntimePreparationService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            ResourceScopeRef? requestedScope = string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef);
            var deployment = await service.PrepareAsync(@namespace, agentName, requestedScope, generation, cancellationToken);
            return Results.Ok(new PrepareAgentRuntimeResponse(
                agentName,
                generation,
                deployment.DeploymentName,
                deployment.RevisionName,
                deployment.State));
        });
}
