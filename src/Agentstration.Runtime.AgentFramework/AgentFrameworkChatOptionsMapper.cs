using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.AgentFramework;

public static class AgentFrameworkChatOptionsMapper
{
    public static ChatOptions Map(ModelChatClientMetadata? model, ModelExecutionOptions? execution)
    {
        var generation = model?.Generation ?? new ModelGenerationOptions();
        var options = new ChatOptions
        {
            ModelId = model?.ModelName,
            Temperature = execution?.Temperature ?? AsFloat(generation.Temperature),
            TopP = AsFloat(execution?.TopP ?? generation.TopP),
            TopK = execution?.TopK ?? generation.TopK,
            MaxOutputTokens = execution?.MaxOutputTokens ?? generation.MaxOutputTokens,
            Seed = execution?.Seed ?? generation.Seed,
            StopSequences = execution?.StopSequences?.ToList() ?? generation.StopSequences?.ToList(),
            ResponseFormat = MapOutput(model?.Output)
        };
        MapReasoning(options, model?.Reasoning);
        return options;
    }

    private static ChatResponseFormat? MapOutput(ModelOutputOptions? output) => output?.Format switch
    {
        null => null,
        ModelOutputFormat.Text => ChatResponseFormat.Text,
        ModelOutputFormat.JsonObject => ChatResponseFormat.Json,
        ModelOutputFormat.JsonSchema when output.JsonSchema is { } schema => ChatResponseFormat.ForJsonSchema(schema, "agentstration_output"),
        ModelOutputFormat.JsonSchema => throw new InvalidOperationException("A JSON schema is required for JsonSchema output."),
        _ => throw new ArgumentOutOfRangeException(nameof(output))
    };

    private static void MapReasoning(ChatOptions options, ModelReasoningOptions? reasoning)
    {
        if (reasoning is null || reasoning.Mode == ReasoningMode.Automatic) return;
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["reasoning_enabled"] = reasoning.Mode == ReasoningMode.Enabled;
        if (reasoning.Effort is { } effort)
            options.AdditionalProperties["reasoning_effort"] = effort.ToString().ToLowerInvariant();
    }

    private static float? AsFloat(double? value) => value is null ? null : checked((float)value.Value);
}
