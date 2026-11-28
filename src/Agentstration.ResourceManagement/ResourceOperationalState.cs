using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.ResourceManagement;

public sealed record ResourceOperationalState(ResourceAccessIdentity Resource, bool Enabled,
    string ETag, DateTimeOffset UpdatedAt);

public interface IResourceOperationalStateStore
{
    Task<ResourceOperationalState?> GetAsync(ResourceAccessIdentity resource, CancellationToken cancellationToken);
    Task<ResourceOperationalState> PutAsync(ResourceAccessIdentity resource, bool enabled, string? ifMatch,
        CancellationToken cancellationToken);
}
