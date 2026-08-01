using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class RetryRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{runId}/retry", HandleAsync);

    private static Task<IResult> HandleAsync(string runId, HttpResponse response, RuntimeRunService service, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var stored = await service.RetryAsync(runId, cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"/api/runtime/runs/{stored.Value.Id}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
