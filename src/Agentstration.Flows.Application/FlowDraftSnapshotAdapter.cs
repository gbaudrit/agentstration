using Agentstration.Flows;

namespace Agentstration.Flows.Application;

internal static class FlowDraftSnapshotAdapter
{
    public static RoutingFlowDefinition ToRoutingDefinition(FlowGraphDefinition graph)
    {
        var router = graph.Steps.OfType<RouterFlowStepDefinition>().FirstOrDefault();
        var destinations = Destinations(graph, router);
        if (destinations.Length == 0)
            destinations = [new FlowTargetReference(FlowTargetKind.Agent, "unconfigured-agent")];
        return CreateRouting(router, destinations);
    }

    public static FlowDefinition ToPublishedDefinition(FlowGraphDefinition graph)
    {
        var router = graph.Steps.OfType<RouterFlowStepDefinition>().FirstOrDefault();
        var destinations = Destinations(graph, router);
        return destinations.Length == 0
            ? new WorkflowFlowDefinition(graph.EntryStep, [new FlowNode(graph.EntryStep, FlowNodeKind.Input)], [])
            : CreateRouting(router, destinations);
    }

    private static FlowTargetReference[] Destinations(FlowGraphDefinition graph, RouterFlowStepDefinition? router) =>
        router?.Candidates
            .Select(candidate => new FlowTargetReference(FlowTargetKind.Agent, candidate.Agent.ResourceId))
            .ToArray()
        ?? graph.Steps.OfType<AgentFlowStepDefinition>()
            .Where(step => !step.Agent.ResourceId.StartsWith("${", StringComparison.Ordinal))
            .Select(step => new FlowTargetReference(FlowTargetKind.Agent, step.Agent.ResourceId))
            .ToArray();

    private static RoutingFlowDefinition CreateRouting(RouterFlowStepDefinition? router, FlowTargetReference[] destinations)
    {
        var fallback = router?.Fallback is null
            ? null
            : new FlowTargetReference(FlowTargetKind.Agent, router.Fallback.ResourceId);
        return new RoutingFlowDefinition(FlowRoutingStrategy.Deterministic, destinations, fallback);
    }
}
