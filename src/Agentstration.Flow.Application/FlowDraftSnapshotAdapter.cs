using Agentstration.Flow;

namespace Agentstration.Flow.Application;

internal static class FlowDraftSnapshotAdapter
{
    public static RoutingFlowSpec ToRoutingSpec(FlowGraphDefinition graph)
    {
        var router = graph.Steps.OfType<RouterFlowStepDefinition>().FirstOrDefault();
        var destinations = router?.Candidates
            .Select(candidate => new FlowTargetReference(FlowTargetKind.Agent, candidate.Agent.ResourceId))
            .ToArray() ?? [];
        if (destinations.Length == 0)
            destinations = [new FlowTargetReference(FlowTargetKind.Agent, "unconfigured-agent")];
        return new RoutingFlowSpec(FlowRoutingStrategy.Deterministic, destinations);
    }
}
