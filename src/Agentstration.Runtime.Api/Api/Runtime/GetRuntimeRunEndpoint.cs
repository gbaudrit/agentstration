using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class GetRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);

    private static Task<IResult> HandleAsync(string runId, string? scopeRef, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var scope = RuntimeHttp.CurrentScope(requestContext,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
            await service.AuthorizeAsync(scope, cancellationToken);
            var stored = await service.GetExactAsync(scope.ScopeRef, runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
            return RuntimeHttp.RunResult(stored, response, StatusCodes.Status200OK);
        });
}
