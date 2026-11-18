using System.Text.Json;

namespace Agentstration.Flows;

/// <summary>Runs an authored process region once for each JSON array element.</summary>
public sealed record ForeachFlowStepDefinition : FlowStepDefinition
{
    public ForeachPartitionDefinition? Partition { get; init; }
    public JsonElement? ItemsMapping { get; init; }
    public int MaximumItems { get; init; } = 32;
    public int MaximumConcurrency { get; init; } = 1;
    public JsonElement? ItemSchema { get; init; }
    public JsonElement? ResultSchema { get; init; }
}
