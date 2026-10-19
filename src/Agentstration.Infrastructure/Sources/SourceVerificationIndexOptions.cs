namespace Agentstration.Infrastructure.Sources;

public sealed record SourceVerificationIndexOptions
{
    public const string SectionName = "Agentstration:Sources:VerificationIndex";
    public string? Url { get; init; }
    public int TimeoutSeconds { get; init; } = 10;
    public int MaximumBytes { get; init; } = 1024 * 1024;
}
