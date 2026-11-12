using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flows;

/// <summary>A capability exposed explicitly by a step executor; it does not prescribe its partitioning.</summary>
public sealed record FlowProcessRegion(string Key, int MaximumUnits, int MaximumConcurrency = 1);

public sealed record FlowProcessDefinitionOrigin(
    FlowId SourceFlowId, ResourceScopeRef SourceScopeRef, string SourceVersion,
    string SourceSnapshotId, string SourceDefinitionHash, string OwnerStepName,
    string? OwnerStepDisplayName, string Region, string DefinitionHash);

public sealed record FlowProcessRunOrigin(
    FlowProcessDefinitionOrigin Definition, string OwnerRunId, string OwnerStepName,
    int OwnerAttempt, Guid? OwnerStepExecutionId, string UnitKey, int Ordinal, string? Descriptor);

public sealed record FlowProcessUnit(string Key, int Ordinal, string? Descriptor = null, int? TotalUnits = null);
public sealed record FlowProcessUnitReference(string Key, int Ordinal, string RunId, string? Descriptor);
public sealed record FlowProcessAdmission(int Attempt, string Region, bool Closed,
    IReadOnlyList<FlowProcessUnitReference> Units)
{
    public int? TotalUnits { get; init; }
    public bool RequiresClosure { get; init; }
}
public sealed record FlowFailureOrigin(string RunId, string StepName, string? UnitKey = null);

[JsonConverter(typeof(JsonStringEnumConverter<FlowProcessPartition>))]
public enum FlowProcessPartition { Batches, TextChunks }

/// <summary>Partitioning owned by the built-in Transform executor; other executors may expose different semantics.</summary>
public sealed record TransformProcessDefinition
{
    public FlowProcessPartition Partition { get; init; } = FlowProcessPartition.Batches;
    public int UnitSize { get; init; } = 1;
    public int MaximumUnits { get; init; } = 32;
    public int MaximumConcurrency { get; init; } = 1;
}

public static class FlowProcessRegions
{
    public static FlowProcessRegion? Region(this FlowStepDefinition step) => step switch
    {
        ArtifactTransformFlowStepDefinition artifact => new("process", artifact.MaximumArtifacts, artifact.MaximumConcurrency ?? 1),
        TransformFlowStepDefinition { Processing: { } process } => new("process", process.MaximumUnits, process.MaximumConcurrency),
        _ => null
    };

    public static IReadOnlyList<JsonElement> Partition(JsonElement input, TransformProcessDefinition process)
    {
        if (process.MaximumUnits is < 1 or > 128 || process.UnitSize is < 1 or > 65536
            || process.MaximumConcurrency is < 1 or > 16 || !Enum.IsDefined(process.Partition))
            throw new InvalidOperationException("Process partition limits are outside their supported bounds.");
        var units = new List<JsonElement>();
        if (process.Partition == FlowProcessPartition.TextChunks && input.ValueKind == JsonValueKind.String)
        {
            var text = input.GetString()!;
            for (var offset = 0; offset < text.Length;)
            {
                var length = Math.Min(process.UnitSize, text.Length - offset);
                // A partition boundary must not split a UTF-16 surrogate pair.
                if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])
                    && char.IsLowSurrogate(text[offset + length])) length++;
                Add(JsonSerializer.SerializeToElement(text.Substring(offset, length)));
                offset += length;
            }
        }
        else if (process.Partition == FlowProcessPartition.Batches && input.ValueKind == JsonValueKind.Array)
        {
            foreach (var batch in input.EnumerateArray().Chunk(process.UnitSize))
                Add(JsonSerializer.SerializeToElement(batch));
        }
        else throw new InvalidOperationException("The input does not match the declared process partition.");
        return units;

        void Add(JsonElement value)
        {
            if (units.Count >= process.MaximumUnits) throw new InvalidOperationException("The process unit limit was exceeded.");
            units.Add(value);
        }
    }
}
