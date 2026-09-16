using Agentstration.Agents.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Api.Runtime;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class RouteAndExecuteEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/routing/invoke", HandleAsync)
        .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);

    private static Task<IResult> HandleAsync(
        RouteAndExecuteRequest body,
        HttpRequest request,
        IAgentExecutionCoordinator service,
        CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            RuntimeHttp.RequireApiVersion(request);
            var result = await service.RouteAndExecuteAsync(body.Input, cancellationToken);
            return Results.Ok(new RouteAndExecuteResponse(result.Route.AgentId, result.Route.Confidence, result.Route.Reason, result.Execution.Output));
        });
}
