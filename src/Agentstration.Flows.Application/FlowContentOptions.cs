namespace Agentstration.Flows.Application;

/// <summary>Operator budget for serialized values passed through Flow composition.</summary>
public sealed record FlowContentOptions
{
    public const string SectionName = "Agentstration:FlowContent";
    public const int DefaultMaximumValueBytes = 10 * 1024 * 1024;
    public int MaximumValueBytes { get; init; } = DefaultMaximumValueBytes;

    public void Validate()
    {
        if (MaximumValueBytes < 1)
            throw new InvalidOperationException($"{SectionName} byte limits must be positive.");
    }
}
