using System.Text.Json;

namespace Agentstration.Aep.Abstractions;

public static class AepConnectedServiceDiscoveryProtocol
{
    public const string Capability = "aep.connected-service-operation-discovery";
    public const string Version = "1.0";
    public const string Path = "/aep/v1/connected-service-operations/discover";
    public const int MaximumResponseBytes = 48 * 1024;
}

// Execution-only envelope. Never retain this request in Flow input, events or diagnostics.
public sealed record AepConnectedServiceDiscoveryRequest(
    string ServiceTypeId, string ServiceTypeVersion, JsonElement Options,
    IReadOnlyList<AepBoundValue> BoundValues, string CorrelationId);

public sealed record AepDiscoveredServiceOperation(
    string ExternalId, string DisplayName, string TypeId, string TypeVersion,
    JsonElement Definition, string? Description = null);

public sealed record AepServiceDiscoveryDiagnostic(string Code, string Message);

public sealed record AepConnectedServiceDiscoveryResponse(
    IReadOnlyList<AepDiscoveredServiceOperation> Operations,
    IReadOnlyList<AepServiceDiscoveryDiagnostic> Diagnostics);
