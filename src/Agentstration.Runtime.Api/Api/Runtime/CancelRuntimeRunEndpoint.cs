using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class CancelRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{runId}/cancel", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanRunAgents);

    private static Task<IResult> HandleAsync(string runId, string? scopeRef, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var scope = RuntimeHttp.CurrentScope(requestContext,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
            await service.AuthorizeAsync(scope, cancellationToken);
            return RuntimeHttp.RunResult(await service.CancelExactAsync(scope.ScopeRef, runId, cancellationToken), response, StatusCodes.Status200OK);
        });
}
