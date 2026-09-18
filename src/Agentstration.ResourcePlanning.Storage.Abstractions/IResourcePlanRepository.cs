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
    Task<ResourcePlanBindingDraftSnapshot?> GetBindingsAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken);
    Task<ResourcePlanBindingDraftSnapshot> SaveBindingsAsync(ResourcePlanBindingDraft draft, string? expectedETag, CancellationToken cancellationToken);
}

public interface IResourceChangeSetRepository
{
    Task<ResourceChangeSetSnapshot> CreateAsync(ResourceChangeSet changeSet, CancellationToken cancellationToken);
    Task<ResourceChangeSetSnapshot?> GetAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken);
    Task<ResourceChangeSetSnapshot?> FindAsync(ResourcePlanScope scope, ResourcePlanId planId, long planRevision, string materializationDigest, CancellationToken cancellationToken);
    Task<ResourceChangeSetPage> ListAsync(ResourcePlanScope scope, ResourcePlanId? planId, int skip, int take, CancellationToken cancellationToken);
    Task<ResourceChangeSetSnapshot> UpdateAsync(ResourceChangeSet changeSet, string expectedETag, CancellationToken cancellationToken);
    Task AddValidationAsync(ResourceChangeSetValidation validation, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceChangeSetValidation>> ListValidationsAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken);
    Task<ResourceChangeSetApplicationSnapshot?> GetApplicationAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken);
    Task<ResourceChangeSetApplicationSnapshot> SaveApplicationAsync(ResourceChangeSetApplication application, string? expectedETag, CancellationToken cancellationToken);
}

public sealed class ResourcePlanConcurrencyException(string message) : Exception(message);
public sealed class ResourcePlanNotFoundException(ResourcePlanId id) : KeyNotFoundException($"Resource Plan '{id}' was not found.");
public sealed class ResourceChangeSetNotFoundException(ResourceChangeSetId id) : KeyNotFoundException($"Resource ChangeSet '{id}' was not found.");
