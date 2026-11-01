using Agentstration.Agents;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class GetAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{name}", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadAgents);
        group.MapGet("/namespaces/{namespace}/agents/{name}", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadAgents);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, string name, string? scopeRef, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), name, scopeRef, request, response, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        string name,
        string? scopeRef,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, name, scopeRef, request, response, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, string name, string? scopeRef, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            ResourceScopeRef? requestedScope = string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef);
            var stored = await service.GetAgentAsync(@namespace, name, requestedScope, cancellationToken)
                ?? throw new ResourceNotFoundException(new(AgentResourceKinds.Agent, name, @namespace));
            return AgentsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
