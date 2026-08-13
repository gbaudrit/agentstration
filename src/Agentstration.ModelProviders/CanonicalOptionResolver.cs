using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.ModelProviders;

public sealed record CanonicalOptionLayer
{
    public ModelGenerationOptions? Generation { get; init; }
    public ModelReasoningOptions? Reasoning { get; init; }
    public ModelOutputOptions? Output { get; init; }
    public StreamingMode? Streaming { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? ProviderOptions { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? RuntimeOptions { get; init; }
}

public sealed record ResolvedCanonicalOptions(
    ModelGenerationOptions Generation,
    ModelReasoningOptions Reasoning,
    ModelOutputOptions Output,
    AgentExecutionOptions Execution,
    IReadOnlyDictionary<string, JsonElement> ProviderOptions,
    IReadOnlyDictionary<string, JsonElement> RuntimeOptions);

public static class CanonicalOptionResolver
{
    public static ResolvedCanonicalOptions Resolve(params CanonicalOptionLayer[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var generation = new ModelGenerationOptions();
        var reasoning = new ModelReasoningOptions();
        var output = new ModelOutputOptions();
        var streaming = StreamingMode.Automatic;
        var providerOptions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var runtimeOptions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            if (layer.Generation is { } next) generation = Merge(generation, next);
            if (layer.Reasoning is { } nextReasoning) reasoning = nextReasoning;
            if (layer.Output is { } nextOutput) output = nextOutput;
            if (layer.Streaming is { } nextStreaming) streaming = nextStreaming;
            MergeSection(providerOptions, layer.ProviderOptions);
            MergeSection(runtimeOptions, layer.RuntimeOptions);
        }
        return new ResolvedCanonicalOptions(
            generation,
            reasoning,
            output,
            new AgentExecutionOptions { Streaming = Map(streaming) },
            providerOptions,
            runtimeOptions);
    }

    private static RuntimeStreamingMode Map(StreamingMode mode) => mode switch
    {
        StreamingMode.Enabled => RuntimeStreamingMode.Enabled,
        StreamingMode.Disabled => RuntimeStreamingMode.Disabled,
        _ => RuntimeStreamingMode.Automatic
    };

    private static ModelGenerationOptions Merge(ModelGenerationOptions current, ModelGenerationOptions next) => new()
    {
        Temperature = next.Temperature ?? current.Temperature,
        TopP = next.TopP ?? current.TopP,
        TopK = next.TopK ?? current.TopK,
        MaxOutputTokens = next.MaxOutputTokens ?? current.MaxOutputTokens,
        Seed = next.Seed ?? current.Seed,
        StopSequences = next.StopSequences ?? current.StopSequences
    };

    private static void MergeSection(IDictionary<string, JsonElement> target, IReadOnlyDictionary<string, JsonElement>? source)
    {
        if (source is null) return;
        foreach (var item in source) target[item.Key] = item.Value.Clone();
    }
}
