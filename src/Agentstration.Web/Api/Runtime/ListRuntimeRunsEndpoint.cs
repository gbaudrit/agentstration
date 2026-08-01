using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class ListRuntimeRunsEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", HandleAsync);

    private static Task<IResult> HandleAsync(string? agentResourceId, int? skip, int? top, RuntimeRunService service, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 100, 1, 1000);
            var values = await service.ListAsync(agentResourceId, actualSkip, actualTop, cancellationToken);
            var agentFilter = string.IsNullOrWhiteSpace(agentResourceId) ? string.Empty : $"&agentResourceId={Uri.EscapeDataString(agentResourceId)}";
            var nextLink = values.Count == actualTop
                ? $"/api/runtime/runs?skip={actualSkip + actualTop}&top={actualTop}{agentFilter}"
                : null;
            return Results.Ok(new RuntimeRunPageResponse(values.Select(value => value.Value).ToArray(), nextLink));
        });
}
