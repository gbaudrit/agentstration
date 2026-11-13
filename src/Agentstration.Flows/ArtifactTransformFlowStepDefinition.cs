using Agentstration.Schemas;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Flows;

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactProcessingMode>))]
public enum ArtifactProcessingMode
{
    [JsonStringEnumMemberName("content")] Content,
    [JsonStringEnumMemberName("access")] Access
}

/// <summary>The process transition enters a private processing region; returning to this step completes that region.</summary>
public sealed record ArtifactTransformFlowStepDefinition : FlowStepDefinition
{
    public ArtifactProcessingMode Mode { get; init; } = ArtifactProcessingMode.Content;
    public JsonElement? ArtifactsMapping { get; init; }
    public JsonElement? ProcessingInputMapping { get; init; }
    public JsonElement? ResultMapping { get; init; }
    public int MaximumArtifacts { get; init; } = 32;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumConcurrency { get; init; }
    public int MaximumContentBytes { get; init; } = 65_536;
    public int MaximumOutputBytes { get; init; } = 8 * 1024 * 1024;
    public DataContractDefinition? OutputContract { get; init; }
    public string? OutputMediaType { get; init; }
    public string? OutputFileName { get; init; }
}

public sealed record FlowArtifactProcessingContext(string OwnerRunId, string OwnerStepName, int Attempt, int Index, bool ContentAccess = true);

/// <summary>Extracts authored processing connections without requiring a separately published Flow.</summary>
public static class FlowArtifactProcessingGraph
{
    public const string InputName = "artifact_processing_input";
    public const string ResultName = "artifact_processing_result";

    public static FlowGraphDefinition Extract(FlowGraphDefinition graph, ArtifactTransformFlowStepDefinition owner) =>
        FlowProcessGraph.Extract(graph, owner, InputName, ResultName);

    public static IReadOnlyList<FlowTransitionDefinition> ExecutionTransitions(FlowGraphDefinition graph) =>
        FlowProcessGraph.ExecutionTransitions(graph);
}
