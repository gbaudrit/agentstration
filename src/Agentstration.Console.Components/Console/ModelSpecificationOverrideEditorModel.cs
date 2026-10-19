using Agentstration.Models;

namespace Agentstration.Web.Console;

public enum ModelOverrideOperation
{
    Inherit,
    Add,
    Remove
}

public sealed class ModelSpecificationOverrideEditorModel
{
    public Dictionary<ModelContentType, ModelOverrideOperation> Input { get; } = EnumValues<ModelContentType>();
    public Dictionary<ModelContentType, ModelOverrideOperation> Output { get; } = EnumValues<ModelContentType>();
    public ModelFeatureSupport? StreamingSupport { get; set; }
    public ModelFeatureSupport? ToolsSupport { get; set; }
    public Dictionary<ModelToolMode, ModelOverrideOperation> ToolModes { get; } = EnumValues<ModelToolMode>();
    public ModelFeatureSupport? StructuredOutputSupport { get; set; }
    public Dictionary<ModelStructuredOutputFormat, ModelOverrideOperation> OutputFormats { get; } = EnumValues<ModelStructuredOutputFormat>();
    public Dictionary<ModelStructuredOutputFormat, bool?> OutputFormatStrict { get; } = EnumValues<ModelStructuredOutputFormat, bool?>();
    public ModelFeatureSupport? ReasoningSupport { get; set; }
    public Dictionary<ReasoningEffort, ModelOverrideOperation> ReasoningEfforts { get; } = EnumValues<ReasoningEffort>();
    public long? ContextTokens { get; set; }
    public long? MaxOutputTokens { get; set; }

    public bool IsEmpty => Input.Values.All(IsInherited)
        && Output.Values.All(IsInherited)
        && StreamingSupport is null
        && ToolsSupport is null
        && ToolModes.Values.All(IsInherited)
        && StructuredOutputSupport is null
        && OutputFormats.Values.All(IsInherited)
        && ReasoningSupport is null
        && ReasoningEfforts.Values.All(IsInherited)
        && ContextTokens is null
        && MaxOutputTokens is null;

    public ModelSpecificationOverride ToOverride() => new()
    {
        Input = Collection(Input),
        Output = Collection(Output),
        Features = new ModelFeatureOverrides
        {
            Streaming = StreamingSupport is null ? null : new ModelStreamingFeatureOverride { Support = StreamingSupport },
            Tools = ToolsSupport is null && ToolModes.Values.All(IsInherited) ? null : new ModelToolsFeatureOverride
            {
                Support = ToolsSupport,
                Modes = Map(ToolModes, _ => new ModelToolModeSpecification())
            },
            StructuredOutput = StructuredOutputSupport is null && OutputFormats.Values.All(IsInherited) ? null : new ModelStructuredOutputFeatureOverride
            {
                Support = StructuredOutputSupport,
                Formats = Map(OutputFormats, value => new ModelStructuredOutputFormatSpecification { SupportsStrict = OutputFormatStrict[value] })
            },
            Reasoning = ReasoningSupport is null && ReasoningEfforts.Values.All(IsInherited) ? null : new ModelReasoningFeatureOverride
            {
                Support = ReasoningSupport,
                Efforts = Map(ReasoningEfforts, _ => new ModelReasoningEffortSpecification())
            }
        },
        Limits = new ModelLimitOverrides { ContextTokens = ContextTokens, MaxOutputTokens = MaxOutputTokens }
    };

    public static ModelSpecificationOverrideEditorModel From(ModelSpecificationOverride? value)
    {
        var result = new ModelSpecificationOverrideEditorModel();
        if (value is null) return result;
        Apply(result.Input, value.Input);
        Apply(result.Output, value.Output);
        result.StreamingSupport = value.Features.Streaming?.Support;
        result.ToolsSupport = value.Features.Tools?.Support;
        Apply(result.ToolModes, value.Features.Tools?.Modes?.Add.Keys, value.Features.Tools?.Modes?.Remove);
        result.StructuredOutputSupport = value.Features.StructuredOutput?.Support;
        Apply(result.OutputFormats, value.Features.StructuredOutput?.Formats?.Add.Keys, value.Features.StructuredOutput?.Formats?.Remove);
        if (value.Features.StructuredOutput?.Formats?.Add is { } formats)
            foreach (var format in formats) result.OutputFormatStrict[format.Key] = format.Value.SupportsStrict;
        result.ReasoningSupport = value.Features.Reasoning?.Support;
        Apply(result.ReasoningEfforts, value.Features.Reasoning?.Efforts?.Add.Keys, value.Features.Reasoning?.Efforts?.Remove);
        result.ContextTokens = value.Limits.ContextTokens;
        result.MaxOutputTokens = value.Limits.MaxOutputTokens;
        return result;
    }

    private static bool IsInherited(ModelOverrideOperation value) => value == ModelOverrideOperation.Inherit;

    private static Dictionary<T, ModelOverrideOperation> EnumValues<T>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(value => value, _ => ModelOverrideOperation.Inherit);

    private static Dictionary<T, TValue> EnumValues<T, TValue>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(value => value, _ => default(TValue)!);

    private static ModelCollectionOverride<T>? Collection<T>(IReadOnlyDictionary<T, ModelOverrideOperation> values) where T : struct, Enum
    {
        var add = values.Where(value => value.Value == ModelOverrideOperation.Add).Select(value => value.Key).ToArray();
        var remove = values.Where(value => value.Value == ModelOverrideOperation.Remove).Select(value => value.Key).ToArray();
        return add.Length == 0 && remove.Length == 0 ? null : new ModelCollectionOverride<T> { Add = add, Remove = remove };
    }

    private static ModelToolModeOverride? Map(
        IReadOnlyDictionary<ModelToolMode, ModelOverrideOperation> values,
        Func<ModelToolMode, ModelToolModeSpecification> create)
    {
        var add = values.Where(value => value.Value == ModelOverrideOperation.Add).ToDictionary(value => value.Key, value => create(value.Key));
        var remove = values.Where(value => value.Value == ModelOverrideOperation.Remove).Select(value => value.Key).ToArray();
        return add.Count == 0 && remove.Length == 0 ? null : new ModelToolModeOverride { Add = add, Remove = remove };
    }

    private static ModelStructuredOutputFormatOverride? Map(
        IReadOnlyDictionary<ModelStructuredOutputFormat, ModelOverrideOperation> values,
        Func<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification> create)
    {
        var add = values.Where(value => value.Value == ModelOverrideOperation.Add).ToDictionary(value => value.Key, value => create(value.Key));
        var remove = values.Where(value => value.Value == ModelOverrideOperation.Remove).Select(value => value.Key).ToArray();
        return add.Count == 0 && remove.Length == 0 ? null : new ModelStructuredOutputFormatOverride { Add = add, Remove = remove };
    }

    private static ModelReasoningEffortOverride? Map(
        IReadOnlyDictionary<ReasoningEffort, ModelOverrideOperation> values,
        Func<ReasoningEffort, ModelReasoningEffortSpecification> create)
    {
        var add = values.Where(value => value.Value == ModelOverrideOperation.Add).ToDictionary(value => value.Key, value => create(value.Key));
        var remove = values.Where(value => value.Value == ModelOverrideOperation.Remove).Select(value => value.Key).ToArray();
        return add.Count == 0 && remove.Length == 0 ? null : new ModelReasoningEffortOverride { Add = add, Remove = remove };
    }

    private static void Apply<T>(Dictionary<T, ModelOverrideOperation> target, ModelCollectionOverride<T>? value) where T : struct, Enum =>
        Apply(target, value?.Add, value?.Remove);

    private static void Apply<T>(Dictionary<T, ModelOverrideOperation> target, IEnumerable<T>? add, IEnumerable<T>? remove) where T : struct, Enum
    {
        if (add is not null) foreach (var item in add) target[item] = ModelOverrideOperation.Add;
        if (remove is not null) foreach (var item in remove) target[item] = ModelOverrideOperation.Remove;
    }
}
