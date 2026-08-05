using Agentstration.Flow;

namespace Agentstration.Web.Components;

public sealed record FlowGraphNode(string Name, string Type, string? Status = null, TimeSpan? Duration = null)
{
    public static IReadOnlyList<FlowGraphNode> From(FlowSpec spec)
    {
        var nodes = new List<FlowGraphNode> { new("Input", "input") };
        if (spec is RoutingFlowSpec) nodes.Add(new("Router", "router"));
        nodes.Add(new("Agent", "agent"));
        nodes.Add(new("Output", "output"));
        return nodes;
    }

    public static IReadOnlyList<FlowGraphNode> From(FlowRun run) => run.Steps.Select(step => new FlowGraphNode(
        step.StepName,
        step.StepType,
        step.Status.ToString(),
        step.StartedAt is not null && step.CompletedAt is not null ? step.CompletedAt - step.StartedAt : null)).ToArray();
}
