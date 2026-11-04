using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Sources;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure;

/// <summary>Configuration for the standalone application's module composition.</summary>
public sealed record AgentstrationCompositionOptions
{
    public required string DataDirectory { get; init; }
    public AiProviderOptions Ai { get; init; } = new("Deterministic", new Uri("http://localhost/"), "deterministic", null);
    public AgentstrationStorageOptions Storage { get; init; } = new();
    public string? ControlPlaneConnectionString { get; init; }
    public string? WorkPlaneConnectionString { get; init; }
    public string? FlowConnectionString { get; init; }
    public string? RuntimeConnectionString { get; init; }
    public bool EnableHostedServices { get; init; } = true;
    public SourceVerificationIndexOptions SourceVerificationIndex { get; init; } = new();
    public SourceRegistryTransportOptions SourceRegistryTransport { get; init; } = new();
    public RuntimeWorkerLeaseOptions RuntimeWorkerLease { get; init; } = new();
    public RuntimeWorkerDispatchOptions RuntimeWorkerDispatch { get; init; } = new();
}
