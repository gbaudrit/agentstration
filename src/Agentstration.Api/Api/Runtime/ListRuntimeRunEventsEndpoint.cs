using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class ListRuntimeRunEventsEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}/eventHistory", HandleAsync)
        .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);

    private static Task<IResult> HandleAsync(
        string runId,
        long? afterSequence,
        RuntimeRunService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () => Results.Ok(await service.ListEventsAsync(
            new WorkspaceId(requestContext.Current.WorkspaceId),
            runId,
            Math.Max(0, afterSequence ?? 0),
            cancellationToken)));
}
