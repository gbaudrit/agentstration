using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class ResourceManagementLifecycleTests
{
    [TestMethod]
    public async Task CreateRunsFamilyValidationBeforeWriting()
    {
        var store = new RecordingStore();
        var service = new ResourceManagementService<TestResource>(store, [new RejectingValidator()]);

        var exception = await Assert.ThrowsExactlyAsync<ResourceValidationException>(
            () => service.CreateAsync(Resource("invalid"), CancellationToken.None));

        Assert.AreEqual("name.invalid", exception.Issues.Single().Code);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task PublishUsesTheImmutableStoreBoundary()
    {
        var store = new RecordingStore();
        var service = new ResourceManagementService<TestResource>(store, [], new Publisher());

        var published = await service.PublishAsync(Resource("draft"), CancellationToken.None);

        Assert.AreEqual("published", published.Value.Metadata.Name);
        Assert.AreEqual(1, store.ImmutableWrites);
        Assert.AreEqual(0, store.Writes);
    }

    private static TestResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = "Test",
        Metadata = new() { Name = name },
        Generation = 1
    };

    private sealed record TestResource : Resource;

    private sealed class RejectingValidator : IResourceValidator<TestResource>
    {
        public Task<IReadOnlyList<ResourceValidationIssue>> ValidateAsync(TestResource resource, ResourceLifecycleOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceValidationIssue>>([new("name.invalid", "The name is invalid.", "metadata.name")]);
    }

    private sealed class Publisher : IResourcePublisher<TestResource>
    {
        public Task<TestResource> PublishAsync(TestResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resource with { Metadata = resource.Metadata with { Name = "published" } });
    }

    private sealed class RecordingStore : IResourceStore
    {
        public int Writes { get; private set; }
        public int ImmutableWrites { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => Task.FromResult<StoredResource<T>?>(null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>([]);

        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            Writes++;
            return Task.FromResult(Stored(resource));
        }

        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource
        {
            ImmutableWrites++;
            return Task.FromResult(Stored(resource));
        }

        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => Task.CompletedTask;

        private static StoredResource<T> Stored<T>(T resource) where T : Resource => new(resource, "etag", DateTimeOffset.UnixEpoch);
    }
}
