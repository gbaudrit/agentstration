using System.Net;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Console;

/// <summary>Agent pilot: preserve exact ownership; other detail screens use their collection.</summary>
public sealed class ConsoleContextSwitchResolver(IConsoleContextHttpClientFactory clients) : IConsoleContextSwitchResolver
{
    public async Task<ConsoleContextSwitchDestination> ResolveAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken)
    {
        try { return await ResolveCoreAsync(request, cancellationToken); }
        catch (AgentstrationApiException exception)
        {
            throw new HttpRequestException("The Console context switch could not be resolved.", exception, exception.StatusCode);
        }
    }

    private async Task<ConsoleContextSwitchDestination> ResolveCoreAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken)
    {
        var targetScope = ResourceScopeRef.Parse(request.TargetScopeRef);
        using var client = clients.CreateClient(nameof(IManagementApiClient));
        // Validate the context separately: a forbidden context must not be mistaken for a hidden resource.
        var target = await ApiResponse.ReadAsync<IdentityConsoleContextResponse>(client, "api/identity/context", targetScope, cancellationToken);
        if (!string.Equals(target.SelectedScopeRef, targetScope.Value, StringComparison.Ordinal))
            throw new InvalidOperationException("The API did not resolve the requested Console context.");

        var parts = request.Location.AbsolutePath.Trim('/').Split('/');
        var query = QueryHelpers.ParseQuery(request.Location.Query);
        string name;
        ResourceNamespace resourceNamespace;
        if (parts is ["agents", var agent] && agent != "new")
        {
            name = Uri.UnescapeDataString(agent);
            resourceNamespace = ResourceNamespace.Parse(query.TryGetValue("namespace", out var ns) ? ns.ToString() : null);
        }
        else if (parts is ["namespaces", var ns, "agents", var agentName])
        {
            name = Uri.UnescapeDataString(agentName);
            resourceNamespace = ResourceNamespace.Parse(Uri.UnescapeDataString(ns));
        }
        else return new(request.FallbackLocation);

        var unavailable = new ConsoleContextSwitchDestination(QueryHelpers.AddQueryString("/agents", "context", targetScope.Value), true);

        var path = resourceNamespace.IsDefault
            ? $"api/agents/{Uri.EscapeDataString(name)}"
            : $"api/namespaces/{Uri.EscapeDataString(resourceNamespace.Value)}/agents/{Uri.EscapeDataString(name)}";
        if (query.TryGetValue("scopeRef", out var owner))
            path = QueryHelpers.AddQueryString(path, "scopeRef", ResourceScopeRef.Parse(owner.ToString()).Value);

        try
        {
            // Resolve legacy links in the source context before changing context. Never rebind a name in the target.
            var source = await ApiResponse.ReadAsync<AgentResource>(client, path, ResourceScopeRef.Parse(request.CurrentScopeRef), cancellationToken);
            if (source.ScopeRef is not { } sourceOwner || source.Uid == Guid.Empty
                || source.Address != ResourceAddress.Create(resourceNamespace, AgentResourceKinds.Agent, name)) return unavailable;
            var exactPath = QueryHelpers.AddQueryString(path.Split('?')[0], "scopeRef", sourceOwner.Value);
            var destination = await ApiResponse.ReadAsync<AgentResource>(client, exactPath, targetScope, cancellationToken);
            if (source.Uid != destination.Uid || source.ScopeRef != destination.ScopeRef || source.Address != destination.Address)
                return unavailable;

            query.Remove("view"); query.Remove("tab"); query.Remove("returnUrl");
            query["scopeRef"] = sourceOwner.Value;
            query["context"] = targetScope.Value;
            return new(QueryHelpers.AddQueryString(request.Location.GetLeftPart(UriPartial.Path), query) + request.Location.Fragment);
        }
        catch (AgentstrationApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return unavailable;
        }
    }
}
