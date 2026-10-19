using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class GetAgentRevisionEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{agentName}/revisions/{revisionName}", HandleAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/namespaces/{namespace}/agents/{agentName}/revisions/{revisionName}", HandleNamespacedAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
    }

    private static Task<IResult> HandleAsync(
        string agentName,
        string revisionName,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, agentName, revisionName, request, response, service, cancellationToken);

    private static Task<IResult> HandleNamespacedAsync(
        string @namespace,
        string agentName,
        string revisionName,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), agentName, revisionName, request, response, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            var stored = await service.GetRevisionAsync(@namespace, agentName, revisionName, cancellationToken);
            return AgentsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
