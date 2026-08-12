using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class CreateAgentRevisionEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/agents/{name}/revisions", HandleAsync);

    private static Task<IResult> HandleAsync(
        string name,
        CreateRevisionRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var stored = await service.CreateRevisionAsync(
                name,
                new AgentDeploymentSpec { Environment = body.Environment, RuntimeProfileName = body.RuntimeProfileName, HostingMode = body.HostingMode },
                cancellationToken);
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });
}
