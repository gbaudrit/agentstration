using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class PurgeAgentRevisionEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{agentName}/revisions/{revisionName}/purge-impact", GetImpactAsync);
        group.MapGet("/namespaces/{namespace}/agents/{agentName}/revisions/{revisionName}/purge-impact", GetNamespacedImpactAsync);
        group.MapDelete("/agents/{agentName}/revisions/{revisionName}", PurgeAsync)
            .RequireAuthorization(AgentstrationPolicies.CanManageAgents);
        group.MapDelete("/namespaces/{namespace}/agents/{agentName}/revisions/{revisionName}", PurgeNamespacedAsync)
            .RequireAuthorization(AgentstrationPolicies.CanManageAgents);
    }

    private static Task<IResult> GetImpactAsync(
        string agentName,
        string revisionName,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        GetImpactCoreAsync(ResourceNamespace.Default, agentName, revisionName, request, service, cancellationToken);

    private static Task<IResult> GetNamespacedImpactAsync(
        string @namespace,
        string agentName,
        string revisionName,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        GetImpactCoreAsync(ResourceNamespace.Parse(@namespace), agentName, revisionName, request, service, cancellationToken);

    private static Task<IResult> GetImpactCoreAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            return Results.Ok(ToResponse(await service.GetRevisionPurgeImpactAsync(@namespace, agentName, revisionName, cancellationToken)));
        });

    private static Task<IResult> PurgeAsync(
        string agentName,
        string revisionName,
        bool? force,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        PurgeCoreAsync(ResourceNamespace.Default, agentName, revisionName, force == true, request, service, cancellationToken);

    private static Task<IResult> PurgeNamespacedAsync(
        string @namespace,
        string agentName,
        string revisionName,
        bool? force,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        PurgeCoreAsync(ResourceNamespace.Parse(@namespace), agentName, revisionName, force == true, request, service, cancellationToken);

    private static Task<IResult> PurgeCoreAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        bool force,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var impact = await service.PurgeRevisionAsync(@namespace, agentName, revisionName,
                ManagementHttp.IfMatch(request), force, cancellationToken);
            return Results.Ok(ToResponse(impact));
        });

    private static AgentRevisionPurgeImpactResponse ToResponse(AgentRevisionPurgeImpact impact) => new(
        impact.Namespace.Value,
        impact.AgentName,
        impact.RevisionName,
        impact.AgentGeneration,
        impact.ProtectedByRetentionPolicy,
        impact.ProtectionReasons,
        impact.DeploymentName,
        new(
            impact.RunUsage.ActiveRunCount,
            impact.RunUsage.WaitingForInputCount,
            impact.RunUsage.HistoricalRunCount,
            impact.RunUsage.ActiveRunIds,
            impact.RunUsage.ActiveRuns.Select(run => new AgentRevisionRunImpactResponse(
                run.RunId,
                run.Status,
                run.PendingInputRequestCount)).ToArray()));
}
