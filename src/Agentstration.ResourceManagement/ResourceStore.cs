using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public interface IResourceDeletionGuard
{
    Task ValidateDeleteAsync(ResourceKey key, CancellationToken cancellationToken);
}

public sealed record StoredResource<T>(T Value, string ETag, DateTimeOffset UpdatedAt) where T : Resource;

public sealed record ResourceInventoryEntry(
    Guid Uid,
    ResourceScopeRef ScopeRef,
    ResourceNamespace Namespace,
    string Kind,
    string Name,
    DateTimeOffset UpdatedAt);

public interface IResourceStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>?> GetByUidAsync<T>(Guid uid, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException("This store does not support UID lookup.");
    Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException("This store does not support exact-scope lookup.");
    Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException("This store does not support exact-scope enumeration.");
    Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException("This store does not support descendant-visible enumeration.");
    Task<IReadOnlyList<ResourceInventoryEntry>> ListExactInventoryAsync(ResourceScopeRef scopeRef, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException("This store does not support exact-scope inventory enumeration.");
    Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource;

    async Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(ResourceNamespace @namespace, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        var matches = new List<StoredResource<T>>();
        var offset = 0;
        const int pageSize = 1000;
        while (matches.Count < skip + take)
        {
            var page = await ListAsync<T>(kind, offset, pageSize, cancellationToken);
            matches.AddRange(page.Where(resource => resource.Value.Namespace == @namespace));
            if (page.Count < pageSize) break;
            offset += page.Count;
        }
        return matches.Skip(skip).Take(take).ToArray();
    }

    async Task<IReadOnlyList<StoredResource<T>>> ListAllAsync<T>(string kind, CancellationToken cancellationToken) where T : Resource
    {
        const int pageSize = 1000;
        var values = new List<StoredResource<T>>();
        while (true)
        {
            var page = await ListAsync<T>(kind, values.Count, pageSize, cancellationToken);
            values.AddRange(page);
            if (page.Count < pageSize) return values;
        }
    }

    Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException("This store does not support exact-scope writes.");
    Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken);
    Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException("This store does not support exact-scope deletes.");
}

public sealed class ResourceConcurrencyException(string message) : Exception(message);
public sealed class AmbiguousResourceException(ResourceKey key) : Exception($"Resource '{key}' exists in more than one ownership scope; use an exact scoped address.");
public sealed class ResourceNotFoundException(ResourceKey key) : Exception($"Resource '{key}' was not found.");
