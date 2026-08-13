using Agentstration.Flow;

namespace Agentstration.Flow.Application;

internal static class FlowDraftSnapshotAdapter
{
    public static RoutingFlowDefinition ToRoutingDefinition(FlowGraphDefinition graph)
    {
        var router = graph.Steps.OfType<RouterFlowStepDefinition>().FirstOrDefault();
        var destinations = router?.Candidates
            .Select(candidate => new FlowTargetReference(FlowTargetKind.Agent, candidate.Agent.ResourceId))
            .ToArray()
            ?? graph.Steps.OfType<AgentFlowStepDefinition>()
                .Where(step => !step.Agent.ResourceId.StartsWith("${", StringComparison.Ordinal))
                .Select(step => new FlowTargetReference(FlowTargetKind.Agent, step.Agent.ResourceId))
                .ToArray();
        if (destinations.Length == 0)
            destinations = [new FlowTargetReference(FlowTargetKind.Agent, "unconfigured-agent")];
        var fallback = router?.Fallback is null
            ? null
            : new FlowTargetReference(FlowTargetKind.Agent, router.Fallback.ResourceId);
        return new RoutingFlowDefinition(FlowRoutingStrategy.Deterministic, destinations, fallback);
    }
}
