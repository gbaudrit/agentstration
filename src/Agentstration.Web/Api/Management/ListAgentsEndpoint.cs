using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Api.Management;

internal sealed class ListAgentsEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", HandleAsync);
        group.MapGet("/namespaces/{namespace}/agents", HandleNamespacedAsync);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, int? skip, int? top, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        int? skip,
        int? top,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, int? skip, int? top, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 100, 1, 1000);
            var values = await service.ListAgentsAsync(@namespace, actualSkip, actualTop, cancellationToken);
            var nextLink = values.Count == actualTop
                ? $"/api/{(@namespace.IsDefault ? string.Empty : $"namespaces/{@namespace.Value}/")}agents?skip={actualSkip + actualTop}&top={actualTop}"
                : null;
            return Results.Ok(new PagedResponse<AgentResource>(values.Select(value => value.Value).ToArray(), nextLink));
        });
}
