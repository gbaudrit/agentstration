using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Console;

/// <summary>Preserves the current location; resource access is checked by the destination's normal load.</summary>
public sealed class ConsoleContextSwitchResolver(IConsoleContextHttpClientFactory clients) : IConsoleContextSwitchResolver
{
    public async Task<ConsoleContextSwitchDestination> ResolveAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var targetScope = ResourceScopeRef.Parse(request.TargetScopeRef);
            using var client = clients.CreateClient(nameof(IManagementApiClient));
            var target = await ApiResponse.ReadAsync<IdentityConsoleContextResponse>(client, "api/identity/context", targetScope, cancellationToken);
            if (target.SelectedScopeRef != targetScope.Value)
                throw new InvalidOperationException("The API did not resolve the requested Console context.");

            var query = QueryHelpers.ParseQuery(request.Location.Query);
            query.Remove("view"); query.Remove("tab"); query.Remove("returnUrl");
            if (request.ResourceOwnerScopeRef is { } owner)
            {
                var exactOwner = ResourceScopeRef.Parse(owner).Value;
                if (query.TryGetValue("scopeRef", out var explicitOwner) && !string.IsNullOrWhiteSpace(explicitOwner.ToString())
                    && explicitOwner.ToString() != exactOwner)
                    throw new InvalidOperationException("The saved resource owner does not match the current location.");
                query["scopeRef"] = exactOwner;
            }
            if (request.ResourcePage && (!query.TryGetValue("scopeRef", out var coordinate) || string.IsNullOrWhiteSpace(coordinate.ToString())))
                return new(request.FallbackLocation);
            query["context"] = targetScope.Value;
            return new(QueryHelpers.AddQueryString(request.Location.GetLeftPart(UriPartial.Path), query) + request.Location.Fragment);
        }
        catch (AgentstrationApiException exception)
        {
            throw new HttpRequestException("The Console context switch could not be resolved.", exception, exception.StatusCode);
        }
    }
}
