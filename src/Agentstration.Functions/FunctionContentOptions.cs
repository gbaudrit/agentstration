namespace Agentstration.Functions;

/// <summary>Operator budget for serialized native function inputs and outputs.</summary>
public sealed record FunctionContentOptions
{
    public const string SectionName = "Agentstration:FunctionContent";
    public const int DefaultMaximumValueBytes = 10 * 1024 * 1024;
    public int MaximumValueBytes { get; init; } = DefaultMaximumValueBytes;

    public void Validate()
    {
        if (MaximumValueBytes < 1)
            throw new InvalidOperationException($"{SectionName} byte limits must be positive.");
    }
}
