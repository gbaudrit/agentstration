using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public static class ResourceManagementResourceKinds
{
    public const string Operation = "ManagementOperation";
}

public sealed record ResourceManagementOperation : Resource
{
    public required ResourceKey Target { get; init; }
    public required string OperationType { get; init; }
    public required OperationStatus OperationStatus { get; init; }
    public int? PercentComplete { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public enum ResourceLifecycleOperation { Create, Update, Publish, Delete, Import, Export }

public sealed record ResourceValidationIssue(string Code, string Message, string? Path = null);

public sealed class ResourceValidationException(IReadOnlyList<ResourceValidationIssue> issues)
    : Exception("The resource is invalid.")
{
    public IReadOnlyList<ResourceValidationIssue> Issues { get; } = issues;
}

public interface IResourceValidator<in TResource> where TResource : Resource
{
    Task<IReadOnlyList<ResourceValidationIssue>> ValidateAsync(TResource resource, ResourceLifecycleOperation operation, CancellationToken cancellationToken);
}

public interface IResourcePublisher<TResource> where TResource : Resource
{
    Task<TResource> PublishAsync(TResource resource, CancellationToken cancellationToken);
}

public sealed class ResourceManagementService<TResource>(
    IResourceStore store,
    IEnumerable<IResourceValidator<TResource>> validators,
    IResourcePublisher<TResource>? publisher = null)
    where TResource : Resource
{
    public async Task<StoredResource<TResource>> CreateAsync(TResource resource, CancellationToken cancellationToken)
    {
        await ValidateAsync(resource, ResourceLifecycleOperation.Create, cancellationToken);
        return await store.PutAsync(resource, null, true, cancellationToken);
    }

    public async Task<StoredResource<TResource>> UpdateAsync(TResource resource, string ifMatch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);
        await ValidateAsync(resource, ResourceLifecycleOperation.Update, cancellationToken);
        return await store.PutAsync(resource, ifMatch, false, cancellationToken);
    }

    public async Task<StoredResource<TResource>> PublishAsync(TResource resource, CancellationToken cancellationToken)
    {
        if (publisher is null) throw new InvalidOperationException($"Resource kind '{resource.Kind}' does not support publication.");
        await ValidateAsync(resource, ResourceLifecycleOperation.Publish, cancellationToken);
        return await store.CreateImmutableAsync(await publisher.PublishAsync(resource, cancellationToken), cancellationToken);
    }

    public async Task DeleteAsync(ResourceKey key, string ifMatch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);
        await store.DeleteAsync(key, ifMatch, cancellationToken);
    }

    private async Task ValidateAsync(TResource resource, ResourceLifecycleOperation operation, CancellationToken cancellationToken)
    {
        var issues = new List<ResourceValidationIssue>();
        foreach (var validator in validators)
            issues.AddRange(await validator.ValidateAsync(resource, operation, cancellationToken));
        if (issues.Count > 0) throw new ResourceValidationException(issues);
    }
}
