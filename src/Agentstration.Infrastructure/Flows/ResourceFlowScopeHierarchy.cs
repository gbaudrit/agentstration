using Agentstration.Flows.Application;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class ResourceFlowScopeHierarchy(IResourceScopeResolver scopes) : IFlowScopeHierarchy
{
    public async Task<IReadOnlyList<ResourceScopeRef>> ListVisibleAsync(
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        var resolved = await scopes.ResolveAsync(consumerScopeRef, cancellationToken)
            ?? throw new InvalidOperationException($"Resource scope '{consumerScopeRef}' was not found.");
        return [resolved.Scope.Ref, .. resolved.Ancestors.Select(value => value.Ref)];
    }
}
