using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class RetryRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{runId}/retry", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanRunAgents);

    private static Task<IResult> HandleAsync(string runId, string? scopeRef, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var stored = await service.RetryAsync(RuntimeHttp.CurrentScope(requestContext,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef)), runId, cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"/api/runtime/runs/{stored.Value.Id}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
