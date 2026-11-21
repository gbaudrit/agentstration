using Agentstration.Resources;

namespace Agentstration.Models;

// Operational observations are separate from the governed provider definition and its ETag.
public sealed record ModelProviderDiscoveryResource : Resource
{
    public const string ResourceKind = "ModelProviderDiscovery";
    public required Guid ProviderUid { get; init; }
    public required long ProviderGeneration { get; init; }
    public ModelProviderDiscoveryObservation Observation { get; init; } = new();
}

public sealed record ModelProviderDiscoveryObservation
{
    public string State { get; init; } = "never";
    public string Health { get; init; } = "unknown";
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public int Failures { get; init; }
    public string? ErrorCode { get; init; }
    public ModelDiscoverySummary? Result { get; init; }
}

public sealed record ModelDiscoverySummary(int Created, int Updated, int Unchanged, int Missing, int Reappeared, int Total);
