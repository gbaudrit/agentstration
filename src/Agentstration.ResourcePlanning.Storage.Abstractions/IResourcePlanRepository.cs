using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.ResourcePlanning.Storage.Abstractions;

public interface IResourcePlanRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ResourcePlanSnapshot> CreateAsync(ResourcePlan plan, CancellationToken cancellationToken);
    Task<ResourcePlanSnapshot?> GetAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken);
    Task<ResourcePlanPage> ListAsync(ResourcePlanScope scope, ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken);
    Task<ResourcePlanSnapshot> UpdateAsync(ResourcePlan plan, string expectedETag, CancellationToken cancellationToken);
    Task AddActivityAsync(ResourcePlanActivity activity, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken);
}

public sealed class ResourcePlanConcurrencyException(string message) : Exception(message);
public sealed class ResourcePlanNotFoundException(ResourcePlanId id) : KeyNotFoundException($"Resource Plan '{id}' was not found.");
