using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class StopDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/deployments/{name}/stop", HandleAsync)
        .RequireAuthorization(AgentstrationPolicies.CanWriteResources);

    private static Task<IResult> HandleAsync(
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteDeploymentActionAsync(name, request, response, service, service.StopAsync, cancellationToken);
}
