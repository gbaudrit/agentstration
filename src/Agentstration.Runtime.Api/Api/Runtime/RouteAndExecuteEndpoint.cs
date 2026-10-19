using Agentstration.Agents.Contracts;
using Agentstration.Identity.Contracts;
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
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            RuntimeHttp.RequireApiVersion(request);
            var scope = RuntimeHttp.CurrentScope(requestContext);
            var result = await service.RouteAndExecuteAsync(scope, body.Input, scope.PrincipalId.ToString("D"), cancellationToken);
            return Results.Ok(new RouteAndExecuteResponse(result.Route.AgentId, result.Route.Confidence, result.Route.Reason, result.Execution.Output));
        });
}
