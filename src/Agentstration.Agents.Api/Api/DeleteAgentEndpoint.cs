using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class DeleteAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapDelete("/agents/{name}", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapDelete("/namespaces/{namespace}/agents/{name}", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, string name, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), name, request, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        string name,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, name, request, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, string name, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            await service.DeleteAgentAsync(@namespace, name, AgentsApiHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });
}
