using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class SourceFlowCompositionResolver(FlowService flows) : IFlowCompositionResolver
{
    public async Task<ResolvedFlowComposition> ResolveAsync(ResourceScopeRef consumer, ResourceNamespace ownerNamespace,
        FlowCallReference orchestration, IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> bindings, CancellationToken cancellationToken)
    {
        var compiler = new FlowCompositionService(flows);
        var template = await compiler.ResolveCallAsync(orchestration, consumer, ownerNamespace, cancellationToken);
        foreach (var binding in bindings.Where(pair => pair.Value.Any(call => call.PassInputDirectly)))
        {
            var point = template.Graph?.Steps.OfType<FlowCompositionPointStepDefinition>().SingleOrDefault(point => point.Point == binding.Key);
            if (point is null || point.AllowEmpty || binding.Value.Count != 1 || !DominatesSuccess(template.Graph!, point.Name))
                throw new FlowValidationException("flow_composition_boundary_invalid", "The governed source boundary must be required on every successful path.");
        }
        var compiled = await compiler.ResolveAsync(template, bindings, consumer, cancellationToken);
        var resource = await flows.GetExactAsync(template.ScopeRef, template.FlowId, cancellationToken);
        return new(orchestration with
        {
            ScopeRef = template.ScopeRef,
            Namespace = template.FlowId.Namespace,
            Version = template.Version,
            VersionStrategy = FlowCallVersionStrategy.Exact,
            DefinitionHash = template.DefinitionHash,
            ResourceUid = resource!.Value.Uid
        },
            compiled.Definition.Graph?.InputSchema?.Clone(), compiled.Definition.Graph?.OutputSchema?.Clone(), compiled.Receipt);
    }
    private static bool DominatesSuccess(FlowGraphDefinition graph, string boundary)
    {
        var outputs = graph.Steps.OfType<OutputFlowStepDefinition>().Where(output => output.Outcome != FlowOutputOutcome.Error)
            .Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(); pending.Enqueue(graph.EntryStep);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var step))
        {
            if (step == boundary || !visited.Add(step)) continue;
            if (outputs.Contains(step)) return false;
            foreach (var transition in graph.Transitions.Where(transition => transition.FromStep == step)) pending.Enqueue(transition.ToStep);
        }
        return true;
    }

}
