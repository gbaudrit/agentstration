using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class CreateRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", HandleAsync);

    private static Task<IResult> HandleAsync(CreateRuntimeRunRequest body, HttpRequest request, HttpResponse response, RuntimeRunService service, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var initiator = request.HttpContext.User.Identity?.Name ?? body.Initiator ?? "local-user";
            var stored = await service.CreateAsync(body.Agent, body.Input, body.Execution, body.Origin, initiator, cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"/api/runtime/runs/{stored.Value.Id}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
