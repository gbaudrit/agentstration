using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public sealed record ResourceScope(
    long Id,
    ResourceScopeRef Ref,
    ResourceScopeKind Kind,
    string TargetKey,
    long? ParentScopeId);

public sealed record ResolvedResourceScope(
    ResourceScope Scope,
    IReadOnlyList<ResourceScope> Ancestors)
{
    public IReadOnlyList<long> VisibleScopeIds => [Scope.Id, .. Ancestors.Select(value => value.Id)];
}

public interface IResourceScopeResolver
{
    Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken);
}

public static class ResourceScopeOwnership
{
    public static ResourceScopeRef RequireScope(this Resource resource, ResourceScopeKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ScopeRef is not { } scopeRef || scopeRef.Kind != expectedKind)
            throw new InvalidOperationException($"Resource '{resource.Address}' must belong to a {expectedKind.ToString().ToLowerInvariant()} scope.");
        return scopeRef;
    }

    public static Guid RequireScopeTargetId(this Resource resource, ResourceScopeKind expectedKind) =>
        resource.RequireScope(expectedKind).TargetId
        ?? throw new InvalidOperationException($"A {expectedKind.ToString().ToLowerInvariant()} scope must have a target ID.");
}
