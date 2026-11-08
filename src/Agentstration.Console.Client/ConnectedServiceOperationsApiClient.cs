using System.Net.Http.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IConnectedServiceOperationsClient
{
    Task<IReadOnlyList<ConnectedServiceOperationResource>> GetAsync(CancellationToken token);
    Task<ResourceSnapshot<ConnectedServiceOperationResource>> SetEnabledAsync(ConnectedServiceOperationResource operation, bool enabled, CancellationToken token);
    Task DiscoverAsync(ConnectedServiceResource service, CancellationToken token);
    Task RefreshDiscoveryAsync(ConnectedServiceResource service, CancellationToken token);
}

public sealed class ConnectedServiceOperationsApiClient(HttpClient http) : IConnectedServiceOperationsClient
{
    public Task<IReadOnlyList<ConnectedServiceOperationResource>> GetAsync(CancellationToken token) =>
        ApiResponse.ReadAsync<IReadOnlyList<ConnectedServiceOperationResource>>(http, "api/connected-service-operations", token);

    public async Task<ResourceSnapshot<ConnectedServiceOperationResource>> SetEnabledAsync(ConnectedServiceOperationResource operation, bool enabled, CancellationToken token)
    {
        var path = $"api/connected-service-operations/{Uri.EscapeDataString(operation.Name)}?scopeRef={Uri.EscapeDataString(operation.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(operation.Namespace.Value)}";
        using var existing = await http.GetAsync(path, token);
        await ApiResponse.EnsureSuccessAsync(existing, token);
        var current = await existing.Content.ReadFromJsonAsync<ConnectedServiceOperationResource>(token);
        if (current?.Uid != operation.Uid || current.Generation != operation.Generation
            || current.Definition.DefinitionDigest != operation.Definition.DefinitionDigest)
            throw new AgentstrationApiException("The reviewed operation revision has changed. Refresh and review it before changing activation.", Guid.NewGuid().ToString("N"));
        using var request = new HttpRequestMessage(HttpMethod.Put, path.Replace("?", "/enabled?", StringComparison.Ordinal))
        { Content = JsonContent.Create(new SetConnectedServiceOperationEnabledRequest(enabled)) };
        request.Headers.TryAddWithoutValidation("If-Match", existing.Headers.ETag?.ToString());
        using var response = await http.SendAsync(request, token);
        await ApiResponse.EnsureSuccessAsync(response, token);
        return new((await response.Content.ReadFromJsonAsync<ConnectedServiceOperationResource>(token))!, response.Headers.ETag!.ToString());
    }

    public async Task DiscoverAsync(ConnectedServiceResource service, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync($"api/connected-services/{Uri.EscapeDataString(service.Name)}/operation-discoveries?scopeRef={Uri.EscapeDataString(service.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(service.Namespace.Value)}",
            new StartConnectedServiceOperationDiscoveryRequest(), token);
        await ApiResponse.EnsureSuccessAsync(response, token);
    }

    public async Task RefreshDiscoveryAsync(ConnectedServiceResource service, CancellationToken token)
    {
        using var response = await http.GetAsync($"api/connected-services/{Uri.EscapeDataString(service.Name)}/operation-discoveries?scopeRef={Uri.EscapeDataString(service.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(service.Namespace.Value)}", token);
        await ApiResponse.EnsureSuccessAsync(response, token);
    }
}
