namespace Agentstration.Schemas;

/// <summary>Operator ceilings for local XML validation and declarative mapping.</summary>
public sealed record XmlContentOptions
{
    public const string SectionName = "Agentstration:XmlContent";
    public int MaximumDocumentBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumDepth { get; init; } = 32;
    public int MaximumNodes { get; init; } = 8192;
    public int MaximumAttributesPerElement { get; init; } = 64;
    public int MaximumSelectedNodes { get; init; } = 8192;
    public int MaximumArrayItems { get; init; } = 100;
    public int MaximumMappingOperations { get; init; } = 32768;

    public void Validate()
    {
        if (MaximumDocumentBytes < 1 || MaximumDepth < 1 || MaximumNodes < 1 || MaximumAttributesPerElement < 1
            || MaximumSelectedNodes < 1 || MaximumArrayItems < 1 || MaximumMappingOperations < 1)
            throw new InvalidOperationException($"{SectionName} limits must be positive.");
    }
}
