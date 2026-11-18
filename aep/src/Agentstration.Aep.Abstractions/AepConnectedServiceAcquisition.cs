using System.Text.Json;

namespace Agentstration.Aep.Abstractions;

public static class AepConnectedServiceAcquisitionProtocol
{
    public const string Capability = "aep.connected-service-operation-acquisition";
    public const string Version = "1.0";
    public const string Path = "/aep/v1/connected-service-operations/acquire";
    public const int MaximumRequestBytes = 128 * 1024;
    public const int MaximumResponseBytes = 48 * 1024;
}

// Execution-only envelope; connection values and grants must never enter durable Flow state.
public sealed record AepConnectedServiceAcquisitionRequest(
    string ServiceTypeId, string ServiceTypeVersion, JsonElement Options,
    IReadOnlyList<AepBoundValue> BoundValues, string OperationTypeId, string OperationTypeVersion,
    JsonElement Definition, JsonElement Parameters, string CorrelationId);

public sealed record AepConnectedServiceAcquisitionResponse(JsonElement Result);
