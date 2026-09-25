using System.Text.Json.Serialization;

namespace Agentstration.Models;

public sealed record ModelIdentity
{
    public string? Publisher { get; init; }
    public string? Model { get; init; }
    public string? Version { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelContentType>))]
public enum ModelContentType
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("audio")] Audio
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelFeatureSupport>))]
public enum ModelFeatureSupport
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
    [JsonStringEnumMemberName("native")] Native,
    [JsonStringEnumMemberName("emulated")] Emulated,
    [JsonStringEnumMemberName("partial")] Partial
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelToolMode>))]
public enum ModelToolMode
{
    [JsonStringEnumMemberName("function")] Function,
    [JsonStringEnumMemberName("parallel")] Parallel
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelStructuredOutputFormat>))]
public enum ModelStructuredOutputFormat
{
    [JsonStringEnumMemberName("jsonObject")] JsonObject,
    [JsonStringEnumMemberName("jsonSchema")] JsonSchema
}

public record ModelFeatureSpecification
{
    public ModelFeatureSupport Support { get; init; } = ModelFeatureSupport.Unknown;
}

public sealed record ModelStreamingFeatureSpecification : ModelFeatureSpecification;

public sealed record ModelToolModeSpecification;

public sealed record ModelToolsFeatureSpecification : ModelFeatureSpecification
{
    public IReadOnlyDictionary<ModelToolMode, ModelToolModeSpecification> Modes { get; init; }
        = new Dictionary<ModelToolMode, ModelToolModeSpecification>();
}

public sealed record ModelStructuredOutputFormatSpecification
{
    public bool? SupportsStrict { get; init; }
}

public sealed record ModelStructuredOutputFeatureSpecification : ModelFeatureSpecification
{
    public IReadOnlyDictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification> Formats { get; init; }
        = new Dictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification>();
}

public sealed record ModelReasoningEffortSpecification;

public sealed record ModelReasoningFeatureSpecification : ModelFeatureSpecification
{
    public IReadOnlyDictionary<ReasoningEffort, ModelReasoningEffortSpecification> Efforts { get; init; }
        = new Dictionary<ReasoningEffort, ModelReasoningEffortSpecification>();
}

public sealed record ModelFeatureSpecifications
{
    public ModelStreamingFeatureSpecification? Streaming { get; init; }
    public ModelToolsFeatureSpecification? Tools { get; init; }
    public ModelStructuredOutputFeatureSpecification? StructuredOutput { get; init; }
    public ModelReasoningFeatureSpecification? Reasoning { get; init; }
}

public sealed record ModelLimits
{
    public long? ContextTokens { get; init; }
    public long? MaxOutputTokens { get; init; }
}

public sealed record ModelSpecification
{
    public IReadOnlyList<ModelContentType>? Input { get; init; }
    public IReadOnlyList<ModelContentType>? Output { get; init; }
    public ModelFeatureSpecifications Features { get; init; } = new();
    public ModelLimits Limits { get; init; } = new();
}

public sealed record ModelCollectionOverride<T>
    where T : struct, Enum
{
    public IReadOnlyList<T> Add { get; init; } = [];
    public IReadOnlyList<T> Remove { get; init; } = [];
}

public record ModelFeatureOverride
{
    public ModelFeatureSupport? Support { get; init; }
}

public sealed record ModelStreamingFeatureOverride : ModelFeatureOverride;

public sealed record ModelToolModeOverride
{
    public IReadOnlyDictionary<ModelToolMode, ModelToolModeSpecification> Add { get; init; }
        = new Dictionary<ModelToolMode, ModelToolModeSpecification>();
    public IReadOnlyList<ModelToolMode> Remove { get; init; } = [];
}

public sealed record ModelToolsFeatureOverride : ModelFeatureOverride
{
    public ModelToolModeOverride? Modes { get; init; }
}

public sealed record ModelStructuredOutputFormatOverride
{
    public IReadOnlyDictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification> Add { get; init; }
        = new Dictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification>();
    public IReadOnlyList<ModelStructuredOutputFormat> Remove { get; init; } = [];
}

public sealed record ModelStructuredOutputFeatureOverride : ModelFeatureOverride
{
    public ModelStructuredOutputFormatOverride? Formats { get; init; }
}

public sealed record ModelReasoningEffortOverride
{
    public IReadOnlyDictionary<ReasoningEffort, ModelReasoningEffortSpecification> Add { get; init; }
        = new Dictionary<ReasoningEffort, ModelReasoningEffortSpecification>();
    public IReadOnlyList<ReasoningEffort> Remove { get; init; } = [];
}

public sealed record ModelReasoningFeatureOverride : ModelFeatureOverride
{
    public ModelReasoningEffortOverride? Efforts { get; init; }
}

public sealed record ModelFeatureOverrides
{
    public ModelStreamingFeatureOverride? Streaming { get; init; }
    public ModelToolsFeatureOverride? Tools { get; init; }
    public ModelStructuredOutputFeatureOverride? StructuredOutput { get; init; }
    public ModelReasoningFeatureOverride? Reasoning { get; init; }
}

public sealed record ModelLimitOverrides
{
    public long? ContextTokens { get; init; }
    public long? MaxOutputTokens { get; init; }
}

public sealed record ModelSpecificationOverride
{
    public ModelCollectionOverride<ModelContentType>? Input { get; init; }
    public ModelCollectionOverride<ModelContentType>? Output { get; init; }
    public ModelFeatureOverrides Features { get; init; } = new();
    public ModelLimitOverrides Limits { get; init; } = new();
}

public static class EffectiveModelSpecificationResolver
{
    public static ModelSpecification Resolve(ModelSpecification observed, ModelSpecificationOverride? @override = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        @override ??= new ModelSpecificationOverride();

        return new ModelSpecification
        {
            Input = ResolveCollection(observed.Input, @override.Input),
            Output = ResolveCollection(observed.Output, @override.Output),
            Features = new ModelFeatureSpecifications
            {
                Streaming = ResolveStreaming(observed.Features.Streaming, @override.Features.Streaming),
                Tools = ResolveTools(observed.Features.Tools, @override.Features.Tools),
                StructuredOutput = ResolveStructuredOutput(observed.Features.StructuredOutput, @override.Features.StructuredOutput),
                Reasoning = ResolveReasoning(observed.Features.Reasoning, @override.Features.Reasoning)
            },
            Limits = new ModelLimits
            {
                ContextTokens = RestrictLimit(observed.Limits.ContextTokens, @override.Limits.ContextTokens, nameof(ModelLimits.ContextTokens)),
                MaxOutputTokens = RestrictLimit(observed.Limits.MaxOutputTokens, @override.Limits.MaxOutputTokens, nameof(ModelLimits.MaxOutputTokens))
            }
        };
    }

    private static IReadOnlyList<T>? ResolveCollection<T>(IReadOnlyList<T>? observed, ModelCollectionOverride<T>? @override)
        where T : struct, Enum
    {
        if (observed is null && @override is null) return null;
        var values = observed?.ToHashSet() ?? [];
        if (@override is not null)
        {
            ValidateDistinct(@override.Add, "add");
            ValidateDistinct(@override.Remove, "remove");
            values.UnionWith(@override.Add);
            values.ExceptWith(@override.Remove);
        }
        return values.Order().ToArray();
    }

    private static ModelStreamingFeatureSpecification? ResolveStreaming(
        ModelStreamingFeatureSpecification? observed,
        ModelStreamingFeatureOverride? @override)
    {
        if (observed is null && @override is null) return null;
        return new ModelStreamingFeatureSpecification { Support = ResolveSupport(observed?.Support, @override?.Support) };
    }

    private static ModelToolsFeatureSpecification? ResolveTools(
        ModelToolsFeatureSpecification? observed,
        ModelToolsFeatureOverride? @override)
    {
        if (observed is null && @override is null) return null;
        var support = ResolveSupport(observed?.Support, @override?.Support);
        var modes = ResolveMap(observed?.Modes, @override?.Modes?.Add, @override?.Modes?.Remove);
        return new ModelToolsFeatureSpecification
        {
            Support = support,
            Modes = CanExposeDetails(support) ? modes : new Dictionary<ModelToolMode, ModelToolModeSpecification>()
        };
    }

    private static ModelStructuredOutputFeatureSpecification? ResolveStructuredOutput(
        ModelStructuredOutputFeatureSpecification? observed,
        ModelStructuredOutputFeatureOverride? @override)
    {
        if (observed is null && @override is null) return null;
        var support = ResolveSupport(observed?.Support, @override?.Support);
        var formats = ResolveMap(observed?.Formats, @override?.Formats?.Add, @override?.Formats?.Remove);
        return new ModelStructuredOutputFeatureSpecification
        {
            Support = support,
            Formats = CanExposeDetails(support) ? formats : new Dictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification>()
        };
    }

    private static ModelReasoningFeatureSpecification? ResolveReasoning(
        ModelReasoningFeatureSpecification? observed,
        ModelReasoningFeatureOverride? @override)
    {
        if (observed is null && @override is null) return null;
        var support = ResolveSupport(observed?.Support, @override?.Support);
        var efforts = ResolveMap(observed?.Efforts, @override?.Efforts?.Add, @override?.Efforts?.Remove);
        return new ModelReasoningFeatureSpecification
        {
            Support = support,
            Efforts = CanExposeDetails(support) ? efforts : new Dictionary<ReasoningEffort, ModelReasoningEffortSpecification>()
        };
    }

    private static ModelFeatureSupport ResolveSupport(ModelFeatureSupport? observed, ModelFeatureSupport? @override)
    {
        var current = observed ?? ModelFeatureSupport.Unknown;
        if (@override is null) return current;
        if (current == ModelFeatureSupport.Unsupported) return current;
        if (current == ModelFeatureSupport.Unknown) return @override.Value;
        return RestrictSupport(current, @override.Value);
    }

    private static ModelFeatureSupport RestrictSupport(ModelFeatureSupport observed, ModelFeatureSupport @override)
    {
        if (@override is ModelFeatureSupport.Unsupported or ModelFeatureSupport.Unknown) return @override;
        if (observed == ModelFeatureSupport.Partial || @override == ModelFeatureSupport.Partial) return ModelFeatureSupport.Partial;
        if (observed == ModelFeatureSupport.Emulated || @override == ModelFeatureSupport.Emulated) return ModelFeatureSupport.Emulated;
        return ModelFeatureSupport.Native;
    }

    private static bool CanExposeDetails(ModelFeatureSupport support) =>
        support is not ModelFeatureSupport.Unsupported;

    private static IReadOnlyDictionary<TKey, TValue> ResolveMap<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? observed,
        IReadOnlyDictionary<TKey, TValue>? add,
        IReadOnlyList<TKey>? remove)
        where TKey : struct, Enum
        where TValue : class
    {
        var result = new Dictionary<TKey, TValue>();
        if (observed is not null)
            foreach (var value in observed) result.Add(value.Key, value.Value);
        if (add is not null)
            foreach (var value in add) result[value.Key] = value.Value;
        if (remove is not null)
        {
            ValidateDistinct(remove, "remove");
            foreach (var value in remove) result.Remove(value);
        }
        return result.OrderBy(value => value.Key).ToDictionary();
    }

    private static long? RestrictLimit(long? observed, long? @override, string name)
    {
        if (observed is <= 0) throw new ArgumentOutOfRangeException(name, "Observed limits must be positive.");
        if (@override is <= 0) throw new ArgumentOutOfRangeException(name, "Override limits must be positive.");
        if (observed is null) return @override;
        if (@override is null) return observed;
        return Math.Min(observed.Value, @override.Value);
    }

    private static void ValidateDistinct<T>(IReadOnlyList<T> values, string operation)
    {
        if (values.Count != values.Distinct().Count())
            throw new ArgumentException($"Model specification override '{operation}' values must be unique.");
    }
}
