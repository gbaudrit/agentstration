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

    public static FlowGraphDefinition Extract(FlowGraphDefinition graph, ArtifactTransformFlowStepDefinition owner)
    {
        var entries = graph.Transitions.Where(edge => edge.FromStep == owner.Name && edge.Event == "process").ToArray();
        if (entries.Length != 1 || entries[0].Condition is not null || entries[0].ToStep == owner.Name)
            throw new InvalidOperationException("Artifact processing requires one unconditional process connection.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(entries[0].ToStep);
        var returned = false;
        while (queue.TryDequeue(out var name))
        {
            if (name == owner.Name) { returned = true; continue; }
            if (!names.Add(name)) continue;
            var step = graph.Steps.SingleOrDefault(value => value.Name == name)
                ?? throw new InvalidOperationException("A processing connection references an unknown step.");
            if (step is InputFlowStepDefinition || name is InputName or ResultName)
                throw new InvalidOperationException("A processing region cannot contain the Flow input or reserved boundary names.");
            if (step is OutputFlowStepDefinition output && output.Outcome != FlowOutputOutcome.Error)
                throw new InvalidOperationException("Successful processing must return to its owning Artifact step.");
            foreach (var edge in graph.Transitions.Where(edge => edge.FromStep == name)) queue.Enqueue(edge.ToStep);
        }
        if (!returned) throw new InvalidOperationException("Artifact processing requires a result connection to its owning step.");
        if (graph.Transitions.Any(edge => names.Contains(edge.ToStep) && !names.Contains(edge.FromStep)
            && !(edge.FromStep == owner.Name && edge.Event == "process")
            && graph.Steps.Single(value => value.Name == edge.ToStep) is not OutputFlowStepDefinition { Outcome: FlowOutputOutcome.Error }))
            throw new InvalidOperationException("Processing steps cannot be entered from outside their owning region.");
        return new()
        {
            EntryStep = InputName,
            Steps = [new InputFlowStepDefinition { Name = InputName },
                .. graph.Steps.Where(step => names.Contains(step.Name)),
                new OutputFlowStepDefinition { Name = ResultName, Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }],
            Transitions = [new("artifact-processing-start", InputName, "completed", entries[0].ToStep),
                .. graph.Transitions.Where(edge => names.Contains(edge.FromStep)).Select(edge =>
                    edge.ToStep == owner.Name ? edge with { ToStep = ResultName } : edge)]
        };
    }

    public static IReadOnlyList<FlowTransitionDefinition> ExecutionTransitions(FlowGraphDefinition graph)
    {
        var regions = graph.Steps.OfType<ArtifactTransformFlowStepDefinition>().ToDictionary(step => step.Name,
            step => Extract(graph, step).Steps.Select(value => value.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return graph.Transitions.Where(edge => !(regions.ContainsKey(edge.FromStep) && edge.Event == "process")
            && !(regions.TryGetValue(edge.ToStep, out var body) && body.Contains(edge.FromStep))).ToArray();
    }
}
