using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

public sealed partial class AepClient
{
    public async Task<AepConnectedServiceAcquisitionResponse> AcquireConnectedServiceOperationAsync(
        AepConnectedServiceAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, AepConnectedServiceAcquisitionProtocol.Path, request,
            cancellationToken, maximumResponseBytes: AepConnectedServiceAcquisitionProtocol.MaximumResponseBytes);
        return await ReadAsync<AepConnectedServiceAcquisitionResponse>(response, cancellationToken,
            AepConnectedServiceAcquisitionProtocol.MaximumResponseBytes);
    }
}
