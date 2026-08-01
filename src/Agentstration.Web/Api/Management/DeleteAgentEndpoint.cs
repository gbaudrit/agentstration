using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Management;

internal sealed class DeleteAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/agents/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            await service.DeleteAgentAsync(ManagementHttp.AgentId(resourceGroup, name), ManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });
}
