namespace Agentstration.Management.Abstractions;

public sealed record StoredResource<T>(T Value, string ETag, DateTimeOffset UpdatedAt) where T : Resource;

public interface IControlPlaneStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredResource<T>?> GetAsync<T>(string resourceId, CancellationToken cancellationToken) where T : Resource;
    Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string resourceType, string? resourceGroup, int skip, int take, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource;
    Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    Task DeleteAsync(string resourceId, string? ifMatch, CancellationToken cancellationToken);
}

public sealed class ControlPlaneConcurrencyException(string message) : Exception(message);
public sealed class ControlPlaneResourceNotFoundException(string resourceId) : Exception($"Resource '{resourceId}' was not found.");
