using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public interface IFlowScopeHierarchy
{
    Task<IReadOnlyList<ResourceScopeRef>> ListVisibleAsync(
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken);
}

public sealed class FlowReferenceAmbiguousException(FlowReference reference)
    : Exception($"Flow reference '{reference.FlowId}' is visible in more than one ownership scope; specify scopeRef.");

public sealed class FlowReferenceOutsideScopeException(ResourceScopeRef ownerScopeRef, ResourceScopeRef consumerScopeRef)
    : Exception($"Flow scope '{ownerScopeRef}' is not visible from consumer scope '{consumerScopeRef}'.");
