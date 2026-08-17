using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class StreamRuntimeRunEventsEndpoint : IRuntimeEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}/events", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);

    private static async Task HandleAsync(string runId, HttpRequest request, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken)
    {
        var workspaceId = new WorkspaceId(requestContext.Current.WorkspaceId);
        if (await service.GetAsync(workspaceId, runId, cancellationToken) is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsJsonAsync(new { title = "run_not_found", detail = $"Runtime run '{runId}' was not found.", status = 404 }, cancellationToken);
            return;
        }

        var afterSequence = long.TryParse(request.Headers["Last-Event-ID"].FirstOrDefault(), out var sequence) ? sequence : 0;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        await foreach (var runEvent in service.ObserveAsync(workspaceId, runId, afterSequence, cancellationToken))
        {
            await response.WriteAsync($"id: {runEvent.Sequence}\n", cancellationToken);
            await response.WriteAsync($"event: {runEvent.Kind}\n", cancellationToken);
            await response.WriteAsync($"data: {JsonSerializer.Serialize(runEvent, JsonOptions)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
    }
}
