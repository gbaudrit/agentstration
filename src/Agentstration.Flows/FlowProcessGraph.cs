using System.Text.Json;

namespace Agentstration.Flows;

/// <summary>Derives execution exclusively from the accepted graph, never from worker-supplied definitions.</summary>
public static class FlowProcessGraph
{
    public static FlowGraphDefinition Extract(FlowGraphDefinition graph, FlowStepDefinition owner, string inputName = "process_input", string resultName = "process_result")
    {
        var region = owner.Region() ?? throw new InvalidOperationException("The step does not expose a process region.");
        if (owner is ArtifactTransformFlowStepDefinition) { inputName = FlowArtifactProcessingGraph.InputName; resultName = FlowArtifactProcessingGraph.ResultName; }
        var entries = graph.Transitions.Where(edge => edge.FromStep == owner.Name && edge.Event == region.Key).ToArray();
        if (entries.Length != 1 || entries[0].Condition is not null || entries[0].ToStep == owner.Name)
            throw new InvalidOperationException("Processing requires one unconditional process connection.");
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
            if (step is InputFlowStepDefinition || (name == inputName || name == resultName))
                throw new InvalidOperationException("A processing region cannot contain the Flow input or reserved boundary names.");
            if (step is OutputFlowStepDefinition output && output.Outcome != FlowOutputOutcome.Error)
                throw new InvalidOperationException("Successful processing must return to its owning step.");
            foreach (var edge in graph.Transitions.Where(edge => edge.FromStep == name)) queue.Enqueue(edge.ToStep);
        }
        if (!returned) throw new InvalidOperationException("Processing requires a result connection to its owning step.");
        if (graph.Transitions.Any(edge => names.Contains(edge.ToStep) && !names.Contains(edge.FromStep)
            && !(edge.FromStep == owner.Name && edge.Event == region.Key)
            && graph.Steps.Single(value => value.Name == edge.ToStep) is not OutputFlowStepDefinition { Outcome: FlowOutputOutcome.Error }))
            throw new InvalidOperationException("Processing steps cannot be entered from outside their owning region.");
        return new()
        {
            EntryStep = inputName,
            Steps = [new InputFlowStepDefinition { Name = inputName },
                .. graph.Steps.Where(step => names.Contains(step.Name)),
                new OutputFlowStepDefinition { Name = resultName, Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }],
            Transitions = [new(owner is ArtifactTransformFlowStepDefinition ? "artifact-processing-start" : "process-start", inputName, "completed", entries[0].ToStep),
                .. graph.Transitions.Where(edge => names.Contains(edge.FromStep)).Select(edge =>
                    edge.ToStep == owner.Name ? edge with { ToStep = resultName } : edge)]
        };
    }

    public static IReadOnlyList<FlowTransitionDefinition> ExecutionTransitions(FlowGraphDefinition graph)
    {
        var regions = graph.Steps.Where(step => step.Region() is not null).ToDictionary(step => step.Name,
            step => FlowProcessGraph.Extract(graph, step).Steps.Select(value => value.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return graph.Transitions.Where(edge => !(regions.ContainsKey(edge.FromStep) && edge.Event == "process")
            && !(regions.TryGetValue(edge.ToStep, out var body) && body.Contains(edge.FromStep))).ToArray();
    }
}
