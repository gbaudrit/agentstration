using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class GetDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/deployments/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var id = ManagementHttp.DeploymentId(resourceGroup, name);
            var stored = await service.GetDeploymentAsync(id, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(id);
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
