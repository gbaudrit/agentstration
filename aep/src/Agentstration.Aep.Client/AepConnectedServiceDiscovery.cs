using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

public sealed partial class AepClient
{
    public async Task<AepConnectedServiceDiscoveryResponse> DiscoverConnectedServiceOperationsAsync(
        AepConnectedServiceDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, AepConnectedServiceDiscoveryProtocol.Path, request, cancellationToken,
            maximumResponseBytes: AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes);
        return await ReadAsync<AepConnectedServiceDiscoveryResponse>(response, cancellationToken, AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes);
    }
}
