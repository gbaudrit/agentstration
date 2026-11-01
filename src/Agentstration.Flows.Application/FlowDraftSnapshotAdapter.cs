using System.Globalization;
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
            .Select(candidate => ToTarget(candidate.Agent))
            .ToArray()
        ?? graph.Steps.OfType<AgentFlowStepDefinition>()
            .Where(step => !step.Agent.ResourceId.StartsWith("${", StringComparison.Ordinal))
            .Select(step => ToTarget(step.Agent))
            .ToArray();

    private static RoutingFlowDefinition CreateRouting(RouterFlowStepDefinition? router, FlowTargetReference[] destinations)
    {
        var fallback = router?.Fallback is null
            ? null
            : ToTarget(router.Fallback);
        return new RoutingFlowDefinition(FlowRoutingStrategy.Deterministic, destinations, fallback);
    }

    private static FlowTargetReference ToTarget(FlowResourceReference reference) => new(
        FlowTargetKind.Agent,
        reference.ResourceId,
        reference.Version?.ToString(CultureInfo.InvariantCulture),
        reference.Namespace,
        reference.ScopeRef,
        reference.ResourceUid,
        reference.RevisionUid,
        reference.DefinitionHash);
}
