using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class ListAgentsEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/agents", HandleAsync);

    private static Task<IResult> HandleAsync(
        int? skip,
        int? top,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 100, 1, 1000);
            var values = await service.ListAgentsAsync(actualSkip, actualTop, cancellationToken);
            var nextLink = values.Count == actualTop
                ? $"/api/agents?skip={actualSkip + actualTop}&top={actualTop}"
                : null;
            return Results.Ok(new PagedResponse<AgentResource>(values.Select(value => value.Value).ToArray(), nextLink));
        });
}
