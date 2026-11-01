using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class ListRuntimeRunEventsEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}/eventHistory", HandleAsync)
        .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);

    private static Task<IResult> HandleAsync(
        string runId,
        string? scopeRef,
        long? afterSequence,
        RuntimeRunService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var scope = RuntimeHttp.CurrentScope(requestContext,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
            await service.AuthorizeReadAsync(scope, cancellationToken);
            return Results.Ok(await service.ListEventsExactAsync(scope.ScopeRef, runId,
                Math.Max(0, afterSequence ?? 0), cancellationToken));
        });
}
