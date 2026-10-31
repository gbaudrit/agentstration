using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class GetAgentRuntimeReadinessEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{agentName}/readiness", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadResources);
    public static void MapNamespaced(RouteGroupBuilder group) => group.MapGet("/{agentName}/readiness", HandleNamespacedAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadResources);

    private static Task<IResult> HandleAsync(
        string agentName,
        long generation,
        string? scopeRef,
        RuntimeRunService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Default, agentName, scopeRef, generation, service, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        long generation,
        string? scopeRef,
        RuntimeRunService service,
        CancellationToken cancellationToken) => ExecuteAsync(ResourceNamespace.Parse(@namespace), agentName, scopeRef, generation, service, cancellationToken);

    private static Task<IResult> ExecuteAsync(
        ResourceNamespace @namespace,
        string agentName,
        string? scopeRef,
        long generation,
        RuntimeRunService service,
        CancellationToken cancellationToken) => RuntimeHttp.ExecuteAsync(async () =>
        {
            if (generation < 1) throw new RuntimeRunValidationException("agent_version_invalid", "Agent generation must be positive.");
            ResourceScopeRef? requestedScope = string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef);
            var readiness = await service.GetReadinessAsync(@namespace, agentName, requestedScope, generation, cancellationToken);
            return Results.Ok(new AgentRuntimeReadinessResponse(
                readiness.AgentId,
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
