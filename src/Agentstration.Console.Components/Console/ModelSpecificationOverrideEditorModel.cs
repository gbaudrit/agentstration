using Agentstration.Models;

namespace Agentstration.Web.Console;

public enum ModelOverrideOperation
{
    Inherit,
    Add,
    Remove
}

public class ModelSpecificationOverrideEditorModel
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

    public ModelSpecificationOverrideEditorModel() { }

    protected ModelSpecificationOverrideEditorModel(ModelSpecificationOverride? value)
    {
        if (value is null) return;
        Apply(Input, value.Input);
        Apply(Output, value.Output);
        StreamingSupport = value.Features.Streaming?.Support;
        ToolsSupport = value.Features.Tools?.Support;
        Apply(ToolModes, value.Features.Tools?.Modes?.Add.Keys, value.Features.Tools?.Modes?.Remove);
        StructuredOutputSupport = value.Features.StructuredOutput?.Support;
        Apply(OutputFormats, value.Features.StructuredOutput?.Formats?.Add.Keys, value.Features.StructuredOutput?.Formats?.Remove);
        if (value.Features.StructuredOutput?.Formats?.Add is { } formats)
            foreach (var format in formats) OutputFormatStrict[format.Key] = format.Value.SupportsStrict;
        ReasoningSupport = value.Features.Reasoning?.Support;
        Apply(ReasoningEfforts, value.Features.Reasoning?.Efforts?.Add.Keys, value.Features.Reasoning?.Efforts?.Remove);
        ContextTokens = value.Limits.ContextTokens;
        MaxOutputTokens = value.Limits.MaxOutputTokens;
    }

    public static ModelSpecificationOverrideEditorModel From(ModelSpecificationOverride? value) => new(value);

    public static bool SetFeatureSupport<TEnum>(ModelFeatureSupport? value, ModelFeatureSupport? discovered,
        Dictionary<TEnum, ModelOverrideOperation> details, Action<ModelFeatureSupport?> setter) where TEnum : struct, Enum
    {
        var cleared = (value == ModelFeatureSupport.Unsupported || value is null && discovered is null or ModelFeatureSupport.Unknown)
            && details.Values.Any(operation => operation == ModelOverrideOperation.Add);
        if (cleared)
            foreach (var detail in details.Where(item => item.Value == ModelOverrideOperation.Add).Select(item => item.Key).ToArray()) details[detail] = ModelOverrideOperation.Inherit;
        setter(value);
        return cleared;
    }

    public static ModelOverrideInteraction SetDetailOperation<TEnum>(TEnum value, ModelOverrideOperation operation,
        Dictionary<TEnum, ModelOverrideOperation> target, ModelFeatureSupport? discovered, Func<ModelFeatureSupport?> support,
        Action<ModelFeatureSupport?> setSupport) where TEnum : struct, Enum
    {
        var interaction = ModelOverrideInteraction.None;
        if (operation == ModelOverrideOperation.Add)
        {
            if (discovered == ModelFeatureSupport.Unsupported) return ModelOverrideInteraction.DiscoveredUnsupported;
            if (support() == ModelFeatureSupport.Unsupported) return ModelOverrideInteraction.OverriddenUnsupported;
            if (support() is null && discovered is null or ModelFeatureSupport.Unknown)
            {
                setSupport(ModelFeatureSupport.Native);
                interaction = ModelOverrideInteraction.ParentSupportEnabled;
            }
        }
        target[value] = operation;
        return interaction;
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

public enum ModelOverrideInteraction
{
    None,
    DiscoveredUnsupported,
    OverriddenUnsupported,
    ParentSupportEnabled
}
