using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class GetAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/agents/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var stored = await service.GetAgentAsync(name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, name));
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
