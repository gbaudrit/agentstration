namespace Agentstration.Infrastructure.Sources;

public sealed record SourceRegistryTransportOptions
{
    public const string SectionName = "Agentstration:Sources:Registries:Transport";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaximumRedirects { get; init; } = 3;
    public int ConnectTimeoutSeconds { get; init; } = 5;
    public int PooledConnectionLifetimeSeconds { get; init; } = 120;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Source registry timeout must be between 1 and 60 seconds.");
        if (MaximumRedirects is < 0 or > 10)
            throw new InvalidOperationException("Source registry redirects must be between 0 and 10.");
        if (ConnectTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Source registry connect timeout must be between 1 and 60 seconds.");
        if (PooledConnectionLifetimeSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Source registry pooled connection lifetime must be between 1 and 3600 seconds.");
    }
}
