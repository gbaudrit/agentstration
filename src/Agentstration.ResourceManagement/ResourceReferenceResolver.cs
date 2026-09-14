using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public sealed class ResourceReferenceAmbiguousException(ResourceReference reference, string kind)
    : Exception($"Resource reference '{kind}/{reference.Name}' is visible in more than one ownership scope; specify scopeRef.");

public sealed class ResourceReferenceOutsideScopeException(ResourceReference reference, ResourceScopeRef consumerScopeRef)
    : Exception($"Resource reference '{reference.ScopeRef}' is not visible from consumer scope '{consumerScopeRef}'.");

public interface IResourceReferenceResolver
{
    Task<StoredResource<T>?> ResolveAsync<T>(
        ResourceReference reference,
        ResourceNamespace ownerNamespace,
        string kind,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken) where T : Resource;
}

public sealed class ResourceReferenceResolver(
    IResourceStore store,
    IResourceScopeResolver scopes) : IResourceReferenceResolver
{
    public async Task<StoredResource<T>?> ResolveAsync<T>(
        ResourceReference reference,
        ResourceNamespace ownerNamespace,
        string kind,
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken) where T : Resource
    {
        ArgumentNullException.ThrowIfNull(reference);
        var consumer = await scopes.ResolveAsync(consumerScopeRef, cancellationToken)
            ?? throw new InvalidOperationException($"Resource scope '{consumerScopeRef}' was not found.");
        var visibleScopeRefs = new HashSet<ResourceScopeRef>(
            [consumer.Scope.Ref, .. consumer.Ancestors.Select(value => value.Ref)]);
        var @namespace = reference.Namespace ?? ownerNamespace;

        if (reference.ScopeRef is { } exactScopeRef)
        {
            if (!visibleScopeRefs.Contains(exactScopeRef))
                throw new ResourceReferenceOutsideScopeException(reference, consumerScopeRef);
            return await store.GetExactAsync<T>(
                ScopedResourceAddress.Create(exactScopeRef, @namespace, kind, reference.Name),
                cancellationToken);
        }

        StoredResource<T>? match = null;
        const int pageSize = 200;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await store.ListVisibleAsync<T>(consumerScopeRef, kind, skip, pageSize, cancellationToken);
            foreach (var candidate in page.Where(value =>
                         value.Value.Namespace == @namespace
                         && string.Equals(value.Value.Name, reference.Name, StringComparison.Ordinal)))
            {
                if (match is not null) throw new ResourceReferenceAmbiguousException(reference, kind);
                match = candidate;
            }
            if (page.Count < pageSize) return match;
        }
    }
}
