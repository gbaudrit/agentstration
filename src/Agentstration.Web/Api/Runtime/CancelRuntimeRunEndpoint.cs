using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class CancelRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{runId}/cancel", HandleAsync);

    private static Task<IResult> HandleAsync(string runId, HttpResponse response, RuntimeRunService service, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () => RuntimeHttp.RunResult(await service.CancelAsync(runId, cancellationToken), response, StatusCodes.Status200OK));
}
