using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class GetRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}", HandleAsync);

    private static Task<IResult> HandleAsync(string runId, HttpResponse response, RuntimeRunService service, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
            return RuntimeHttp.RunResult(stored, response, StatusCodes.Status200OK);
        });
}
