using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class CreateAgentRevisionEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/agents/{name}/revisions", HandleAsync)
        .RequireAuthorization(AgentstrationPolicies.CanWriteResources);

    private static Task<IResult> HandleAsync(
        string name,
        CreateRevisionRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            var stored = await service.CreateRevisionAsync(
                name,
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
