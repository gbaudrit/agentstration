using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class ListAgentsEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadAgents);
        group.MapGet("/namespaces/{namespace}/agents", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadAgents);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, int? skip, int? top, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        bool? allNamespaces,
        int? skip,
        int? top,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(allNamespaces is true ? null : ResourceNamespace.Default, skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace? @namespace, int? skip, int? top, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 100, 1, 1000);
            var values = @namespace is null
                ? await service.ListAllAgentsAsync(actualSkip, actualTop, cancellationToken)
                : await service.ListAgentsAsync(@namespace.Value, actualSkip, actualTop, cancellationToken);
            var prefix = @namespace is null
                ? "/api/agents?allNamespaces=true&"
                : @namespace.Value.IsDefault
                    ? "/api/agents?"
                    : $"/api/namespaces/{@namespace.Value.Value}/agents?";
            var nextLink = values.Count == actualTop
                ? $"{prefix}skip={actualSkip + actualTop}&top={actualTop}"
                : null;
            return Results.Ok(new PagedResponse<AgentResource>(values.Select(value => value.Value).ToArray(), nextLink));
        });
}
