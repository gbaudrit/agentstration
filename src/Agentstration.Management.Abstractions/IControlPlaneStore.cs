namespace Agentstration.Management.Abstractions;

public interface IManagementResourceDeletionGuard
{
    Task ValidateDeleteAsync(ResourceKey key, CancellationToken cancellationToken);
}

public sealed record StoredResource<T>(T Value, string ETag, DateTimeOffset UpdatedAt) where T : Resource;

public interface IControlPlaneStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>?> GetAsync<T>(string name, CancellationToken cancellationToken) where T : Resource =>
        GetAsync<T>(new ResourceKey(ResourceKinds.For<T>(), name), cancellationToken);
    Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken);
}

public interface IModelProfileReferenceValidator
{
    Task ValidateAsync(ResourceReference profileReference, CancellationToken cancellationToken);
}

public sealed class ControlPlaneConcurrencyException(string message) : Exception(message);
public sealed class ControlPlaneResourceNotFoundException : Exception
{
    public ControlPlaneResourceNotFoundException(ResourceKey key) : base($"Resource '{key}' was not found.") { }
}
