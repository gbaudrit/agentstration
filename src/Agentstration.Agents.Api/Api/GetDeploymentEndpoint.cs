using Agentstration.Agents;
using Agentstration.ResourceManagement;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class GetDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/deployments/{name}", HandleAsync)
        .RequireAuthorization(AgentstrationPolicies.CanReadResources);

    private static Task<IResult> HandleAsync(
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            var stored = await service.GetDeploymentAsync(name, cancellationToken)
                ?? throw new ResourceNotFoundException(new(AgentResourceKinds.AgentDeployment, name));
            return AgentsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
