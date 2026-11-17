namespace Agentstration.DataSources;

/// <summary>Bounds acquisition bodies and serialized operation values independently.</summary>
public sealed record DataSourceContentOptions
{
    public const string SectionName = "Agentstration:DataSourceContent";
    public const int DefaultMaximumValueBytes = 10 * 1024 * 1024;
    public int MaximumValueBytes { get; init; } = DefaultMaximumValueBytes;
    public int MaximumHttpResponseBytes { get; init; } = 10 * 1024 * 1024;

    public void Validate()
    {
        if (MaximumValueBytes < 1 || MaximumHttpResponseBytes < 1)
            throw new InvalidOperationException($"{SectionName} byte limits must be positive.");
    }
}
