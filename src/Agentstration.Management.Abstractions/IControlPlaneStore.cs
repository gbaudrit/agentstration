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
    Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource;
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
    Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken);
}

public interface IAgentResourceQueries
{
    Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken);
    Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken);
    Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(string revisionName, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(string agentName, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken);
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
