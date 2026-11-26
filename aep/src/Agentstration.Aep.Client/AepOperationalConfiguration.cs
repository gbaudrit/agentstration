using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

public sealed partial class AepClient
{
    public async Task<AepOperationalConfigurationState?> GetOperationalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.TryGetValue(AepOperationalConfigurationProtocol.Capability, out var capability)) return null;
        if (capability.Version != AepOperationalConfigurationProtocol.Version || capability.Endpoint != AepOperationalConfigurationProtocol.Path)
            throw new AepProtocolException("configuration_incompatible", "The operational configuration capability is incompatible.");
        using var response = await SendAsync(HttpMethod.Get, AepOperationalConfigurationProtocol.Path, null, cancellationToken, AepOperationalConfigurationProtocol.MaximumBytes);
        var state = await ReadAsync<AepOperationalConfigurationState>(response, cancellationToken, AepOperationalConfigurationProtocol.MaximumBytes);
        if (state.Descriptor is null || !state.Descriptor.IsValid() || state.AppliedValues is null) throw new AepProtocolException("configuration_invalid", "The operational configuration descriptor is invalid.");
        return state;
    }

    public async Task<AepOperationalConfigurationState> ApplyOperationalConfigurationAsync(AepOperationalConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, AepOperationalConfigurationProtocol.Path, request, cancellationToken, AepOperationalConfigurationProtocol.MaximumBytes);
        var state = await ReadAsync<AepOperationalConfigurationState>(response, cancellationToken, AepOperationalConfigurationProtocol.MaximumBytes);
        if (state.Descriptor is null || !state.Descriptor.IsValid() || state.AppliedValues is null
            || state.Descriptor.Id != request.OptionSet || state.Descriptor.Version != request.Version
            || state.Descriptor.SchemaDigest != request.SchemaDigest || state.AppliedRevision != request.Revision)
            throw new AepProtocolException("configuration_acknowledgment_invalid", "The operational configuration acknowledgment is invalid.");
        return state;
    }
}
