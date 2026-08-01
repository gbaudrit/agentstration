using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class StopDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/deployments/{name}/stop", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteDeploymentActionAsync(resourceGroup, name, request, response, service, service.StopAsync, cancellationToken);
}
