using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceProviderManagementTests
{
    [TestMethod]
    public async Task SourceProviderBindsExtensionRegistrationAndContributionWithoutOwningEndpoint()
    {
        var store = new MemoryStore();
        await store.PutAsync(Extension(), null, true, default);
        var service = new SourceProviderManagementService(store);

        var created = await service.CreateAsync(SourceProvider(), default);

        Assert.AreEqual("source-extension", created.Value.Definition.Extension.Name);
        Assert.AreEqual("git", created.Value.Definition.ContributionId);
        Assert.AreEqual(1, created.Value.Generation);
        Assert.AreEqual(ProvisioningState.Succeeded, created.Value.Status.ProvisioningState);
        Assert.IsFalse(typeof(SourceProviderProperties).GetProperties().Any(value => value.PropertyType == typeof(Uri)));
    }

    [TestMethod]
    public async Task SourceProviderRejectsMissingAndCrossWorkspaceExtensionBindings()
    {
        var store = new MemoryStore();
        var service = new SourceProviderManagementService(store);

        await Assert.ThrowsAsync<SourceProviderValidationException>(() => service.CreateAsync(SourceProvider(), default));
        await store.PutAsync(Extension(), null, true, default);
        var crossWorkspace = SourceProvider() with
        {
            Definition = SourceProvider().Definition with
            {
                Extension = new ResourceReference("source-extension", workspaceRef: "another")
            }
        };
        await Assert.ThrowsAsync<SourceProviderValidationException>(() => service.CreateAsync(crossWorkspace, default));
    }

    [TestMethod]
    public async Task ExtensionRegistrationCannotBeDeletedWhileSourceProviderReferencesIt()
    {
        var store = new MemoryStore();
        var registration = new ExtensionRegistrationManagementService(store);
        await registration.CreateAsync(Extension(), default);
        await new SourceProviderManagementService(store).CreateAsync(SourceProvider(), default);

        var exception = await Assert.ThrowsAsync<ExtensionRegistrationInUseException>(() =>
            registration.DeleteAsync(ResourceNamespace.Default, "source-extension", null, default));

        Assert.AreEqual(ResourceKinds.SourceProvider, exception.Usages.Single().Kind);
    }

    private static ExtensionRegistrationResource Extension() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ExtensionRegistration,
        Metadata = new ResourceMetadata { Name = "source-extension" },
        Definition = new ExtensionRegistrationProperties
        {
            DisplayName = "Source extension",
            Endpoint = new Uri("http://127.0.0.1:5300")
        }
    };

    private static SourceProviderResource SourceProvider() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.SourceProvider,
        Metadata = new ResourceMetadata { Name = "git-local" },
        Definition = new SourceProviderProperties
        {
            DisplayName = "Local Git provider",
            Extension = new("source-extension"),
            ContributionId = "git"
        }
    };

    private sealed class MemoryStore : IControlPlaneStore
    {
        private readonly Dictionary<ResourceKey, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(key, out var entry) && entry.Value is T typed
                ? new StoredResource<T>(typed, entry.ETag, entry.At)
                : null);

        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values
                .Where(value => value.Key.Kind == kind && value.Value.Value is T)
                .Select(value => new StoredResource<T>((T)value.Value.Value, value.Value.ETag, value.Value.At))
                .Skip(skip)
                .Take(take)
                .ToArray());

        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = new ResourceKey(resource.Kind, resource.Name, resource.Namespace);
            if (ifNoneMatch && values.ContainsKey(key)) throw new ControlPlaneConcurrencyException("Already exists.");
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var value = resource.WithSystemState(resource.Uid == Guid.Empty ? Guid.NewGuid() : resource.Uid, resource.TenantId, resource.WorkspaceId, etag);
            values[key] = (value, etag, DateTimeOffset.UnixEpoch);
            return Task.FromResult(new StoredResource<T>((T)value, etag, DateTimeOffset.UnixEpoch));
        }

        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
            PutAsync(resource, null, true, cancellationToken);

        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
