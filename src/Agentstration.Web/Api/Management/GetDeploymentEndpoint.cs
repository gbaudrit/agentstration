using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
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
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var stored = await service.GetDeploymentAsync(name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.AgentDeployment, name));
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
