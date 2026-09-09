using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceProviderManagementTests
{
    [TestMethod]
    public async Task SourceProviderBindsInstanceExtensionRegistrationAndContributionWithoutOwningEndpoint()
    {
        using var fixture = new Fixture();
        await fixture.Registrations.CreateAsync(Extension(), default);

        var created = await fixture.SourceProviders.CreateAsync(SourceProvider(), default);

        Assert.AreEqual(ResourceScopeRef.Instance, created.Value.ScopeRef);
        Assert.AreEqual("source-extension", created.Value.Definition.Extension.Name);
        Assert.AreEqual("git", created.Value.Definition.ContributionId);
        Assert.AreEqual(1, created.Value.Generation);
        Assert.AreEqual(ProvisioningState.Succeeded, created.Value.Status.ProvisioningState);
        Assert.IsFalse(typeof(SourceProviderProperties).GetProperties().Any(value => value.PropertyType == typeof(Uri)));
    }

    [TestMethod]
    public async Task SourceProviderRejectsMissingOrNonInstanceExtensionBindings()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<SourceProviderValidationException>(() =>
            fixture.SourceProviders.CreateAsync(SourceProvider(), default));
        await fixture.Registrations.CreateAsync(Extension(), default);
        var tenantReference = SourceProvider() with
        {
            Definition = SourceProvider().Definition with
            {
                Extension = new ResourceReference(
                    "source-extension",
                    ResourceScopeRef.Tenant(Guid.NewGuid()))
            }
        };

        await Assert.ThrowsAsync<ResourceReferenceOutsideScopeException>(() =>
            fixture.SourceProviders.CreateAsync(tenantReference, default));
    }

    [TestMethod]
    public async Task SourceProviderRejectsNonInstanceOwnership()
    {
        using var fixture = new Fixture();
        await fixture.Registrations.CreateAsync(Extension(), default);
        var tenantOwned = SourceProvider() with
        {
            ScopeRef = ResourceScopeRef.Tenant(Guid.NewGuid())
        };

        await Assert.ThrowsAsync<ResourceScopePolicyException>(() =>
            fixture.SourceProviders.CreateAsync(tenantOwned, default));
    }

    [TestMethod]
    public async Task ExtensionRegistrationUsageIncludesReferencingSourceProvider()
    {
        using var fixture = new Fixture();
        await fixture.Registrations.CreateAsync(Extension(), default);
        await fixture.SourceProviders.CreateAsync(SourceProvider(), default);

        var usages = await fixture.Registrations.GetUsagesAsync(
            ResourceNamespace.Default,
            "source-extension",
            default);

        Assert.AreEqual(ResourceKinds.SourceProvider, usages.Single().Kind);
    }

    [TestMethod]
    public async Task ReferencingSourceBindingIsReportedAndPreventsDeletion()
    {
        using var fixture = new Fixture();
        await fixture.Registrations.CreateAsync(Extension(), default);
        var provider = await fixture.SourceProviders.CreateAsync(SourceProvider(), default);
        await fixture.Store.PutExactAsync(ResourceScopeRef.Instance, new SourceConfigurationResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceConfiguration,
            Metadata = new ResourceMetadata { Name = "catalog", Namespace = new("agentstration") },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new SourceConfigurationProperties
            {
                SourceUid = Guid.NewGuid(),
                DisplayName = "Catalog",
                Bindings = [new SourceBindingSelection
                {
                    Name = "git-distribution",
                    TargetKind = SourceKinds.SourceProvider,
                    Target = new(provider.Value.Name, ResourceScopeRef.Instance, provider.Value.Namespace)
                }]
            }
        }, null, true, default);

        var usage = (await fixture.SourceProviders.GetUsagesAsync(ResourceNamespace.Default, provider.Value.Name, default)).Single();

        Assert.AreEqual("agentstration", usage.Publisher);
        Assert.AreEqual("catalog", usage.SourceName);
        Assert.AreEqual("git-distribution", usage.BindingName);
        await Assert.ThrowsAsync<SourceProviderInUseException>(() =>
            fixture.SourceProviders.DeleteAsync(ResourceNamespace.Default, provider.Value.Name, provider.ETag, default));
    }

    [TestMethod]
    public async Task StatusValidatesTheAdvertisedSourceProviderContribution()
    {
        var inspector = new FakeInspector();
        using var fixture = new Fixture([inspector]);
        await fixture.Registrations.CreateAsync(Extension(), default);
        await fixture.SourceProviders.CreateAsync(SourceProvider(), default);

        var available = await fixture.SourceProviders.GetStatusAsync(ResourceNamespace.Default, "git-local", default);
        inspector.IncludeContribution = false;
        var incompatible = await fixture.SourceProviders.GetStatusAsync(ResourceNamespace.Default, "git-local", default);

        Assert.AreEqual("available", available.Status);
        Assert.AreEqual("incompatible", incompatible.Status);
    }

    private static ExtensionRegistrationResource Extension() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ExtensionRegistration,
        Metadata = new ResourceMetadata { Name = "source-extension" },
        ScopeRef = ResourceScopeRef.Instance,
        Definition = new ExtensionRegistrationProperties
        {
            DisplayName = "Source extension",
            Endpoint = new Uri("http://127.0.0.1:5300"),
            Source = ExtensionRegistrationSource.Configuration
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

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable systemScope;

        public Fixture(IEnumerable<IExtensionInspector>? inspectors = null)
        {
            var context = new CurrentRequestContext();
            systemScope = context.PushSystem();
            Store = new MemoryStore();
            var scopes = new InstanceScopeResolver();
            var references = new ResourceReferenceResolver(Store, scopes);
            var operations = new ResourceScopeOperationService(
                context,
                context,
                null!,
                null!,
                null!,
                scopes);
            Registrations = new ExtensionRegistrationManagementService(Store, references, operations);
            SourceProviders = new SourceProviderManagementService(Store, references, operations, inspectors ?? [], TimeProvider.System);
        }

        public MemoryStore Store { get; }
        public ExtensionRegistrationManagementService Registrations { get; }
        public SourceProviderManagementService SourceProviders { get; }
        public void Dispose() => systemScope.Dispose();
    }

    private sealed class FakeInspector : IExtensionInspector
    {
        public bool IncludeContribution { get; set; } = true;
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;
        public ValueTask<ExtensionInspection> InspectAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
        public ValueTask<ExtensionInspection> InspectAsync(string registrationName, Uri endpoint, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new("source-extension", "Source extension", "1.0.0", null),
                IncludeContribution ? [new("source-provider", "git")] : [],
                []));
    }

    private sealed class InstanceScopeResolver : IResourceScopeResolver
    {
        private static readonly ResourceScope Instance = new(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);

        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedResourceScope?>(scopeRef == ResourceScopeRef.Instance
                ? new(Instance, [])
                : null);
    }

    private sealed class MemoryStore : IControlPlaneStore
    {
        private readonly Dictionary<ScopedResourceAddress, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource =>
            GetExactAsync<T>(key.AtScope(ResourceScopeRef.Instance), cancellationToken);

        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(address, out var entry) && entry.Value is T typed
                ? new StoredResource<T>(typed, entry.ETag, entry.At)
                : null);

        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            ListExactAsync<T>(ResourceScopeRef.Instance, kind, skip, take, cancellationToken);

        public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values
                .Where(value => value.Key.ScopeRef == scopeRef && value.Key.Address.Kind == kind && value.Value.Value is T)
                .Select(value => new StoredResource<T>((T)value.Value.Value, value.Value.ETag, value.Value.At))
                .Skip(skip)
                .Take(take)
                .ToArray());

        public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            ListExactAsync<T>(targetScopeRef, kind, skip, take, cancellationToken);

        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource =>
            PutExactAsync(resource.ScopeRef ?? ResourceScopeRef.Instance, resource, ifMatch, ifNoneMatch, cancellationToken);

        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = ScopedResourceAddress.Create(scopeRef, resource.Namespace, resource.Kind, resource.Name);
            if (ifNoneMatch && values.ContainsKey(key)) throw new ControlPlaneConcurrencyException("Already exists.");
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var value = resource.WithSystemState(resource.Uid == Guid.Empty ? Guid.NewGuid() : resource.Uid, scopeRef, etag);
            values[key] = (value, etag, DateTimeOffset.UnixEpoch);
            return Task.FromResult(new StoredResource<T>((T)value, etag, DateTimeOffset.UnixEpoch));
        }

        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
            PutAsync(resource, null, true, cancellationToken);

        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) =>
            DeleteExactAsync(key.AtScope(ResourceScopeRef.Instance), ifMatch, cancellationToken);

        public Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken)
        {
            values.Remove(address);
            return Task.CompletedTask;
        }
    }
}
