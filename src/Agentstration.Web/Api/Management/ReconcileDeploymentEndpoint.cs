using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class ReconcileDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/deployments/{name}/reconcile", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteDeploymentActionAsync(resourceGroup, name, request, response, service, service.ReconcileAsync, cancellationToken);
}
