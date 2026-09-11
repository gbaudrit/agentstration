using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public sealed record SourceProviderProperties
{
    public required string DisplayName { get; init; }
    public required ResourceReference Extension { get; init; }
    public required string ContributionId { get; init; }
}

public sealed record SourceProviderResource : Resource
{
    public SourceProviderProperties Definition { get; init; } = null!;
}
