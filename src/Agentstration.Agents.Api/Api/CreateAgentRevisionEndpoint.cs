using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class CreateAgentRevisionEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/agents/{name}/revisions", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/namespaces/{namespace}/agents/{name}/revisions", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, string name, string? scopeRef,
        CreateRevisionRequest body, HttpRequest request, HttpResponse response, AgentManagementService service,
        CancellationToken cancellationToken) => HandleCoreAsync(ResourceNamespace.Parse(@namespace), name,
            scopeRef, body, request, response, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        string name,
        string? scopeRef,
        CreateRevisionRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, name, scopeRef, body, request, response, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, string name, string? scopeRef,
        CreateRevisionRequest body, HttpRequest request, HttpResponse response, AgentManagementService service,
        CancellationToken cancellationToken) => AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            var stored = await service.CreateRevisionAsync(
                @namespace,
                name,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef),
                new AgentDeploymentSpec
                {
                    Environment = body.Environment,
                    RuntimeProfileName = body.RuntimeProfileName,
                    RuntimeProfileNamespace = ResourceNamespace.Parse(body.RuntimeProfileNamespace),
                    HostingMode = body.HostingMode
                },
                cancellationToken);
            return AgentsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });
}
