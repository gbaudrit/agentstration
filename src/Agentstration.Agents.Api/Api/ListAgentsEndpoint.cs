using Agentstration.Agents;
using Agentstration.Api.Contracts;
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
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), null, skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        bool? allNamespaces,
        string? scopeRef,
        int? skip,
        int? top,
        HttpRequest request,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(allNamespaces is true ? null : ResourceNamespace.Default, scopeRef, skip, top, request, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace? @namespace, string? scopeRef, int? skip, int? top, HttpRequest request, AgentManagementService service, CancellationToken cancellationToken) =>
        AgentsApiHttp.ExecuteAsync(async () =>
        {
            AgentsApiHttp.RequireApiVersion(request);
            var actualSkip = Math.Max(0, skip ?? 0);
            var actualTop = Math.Clamp(top ?? 100, 1, 1000);
            var catalogScopeRef = @namespace is null ? scopeRef : null;
            var values = catalogScopeRef is not null
                ? await service.ListVisibleAgentsAsync(ResourceScopeRef.Parse(catalogScopeRef), actualSkip, actualTop, cancellationToken)
                : @namespace is null
                    ? await service.ListAllAgentsAsync(actualSkip, actualTop, cancellationToken)
                    : await service.ListAgentsAsync(@namespace.Value, actualSkip, actualTop, cancellationToken);
            var prefix = @namespace is null
                ? "/api/agents?allNamespaces=true&"
                : @namespace.Value.IsDefault
                    ? "/api/agents?"
                    : $"/api/namespaces/{@namespace.Value.Value}/agents?";
            var scopeQuery = catalogScopeRef is null ? string.Empty : $"scopeRef={Uri.EscapeDataString(catalogScopeRef)}&";
            var nextLink = values.Count == actualTop
                ? $"{prefix}{scopeQuery}skip={actualSkip + actualTop}&top={actualTop}"
                : null;
            return Results.Ok(new PagedResponse<AgentResource>(values.Select(value => value.Value).ToArray(), nextLink));
        });
}
