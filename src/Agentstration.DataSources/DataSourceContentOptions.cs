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
        if (MaximumValueBytes is < 1 or > 16 * 1024 * 1024
            || MaximumHttpResponseBytes is < 1 or > 16 * 1024 * 1024)
            throw new InvalidOperationException($"{SectionName} byte limits must be between 1 and 16777216.");
    }
}
