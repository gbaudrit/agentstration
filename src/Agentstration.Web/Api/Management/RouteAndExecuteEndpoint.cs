using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class RouteAndExecuteEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/routing/invoke", HandleAsync);

    private static Task<IResult> HandleAsync(
        RouteAndExecuteRequest body,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var result = await service.RouteAndExecuteAsync(body.Input, cancellationToken);
            return Results.Ok(new RouteAndExecuteResponse(result.Route.AgentId, result.Route.Confidence, result.Route.Reason, result.Execution.Output));
        });
}
