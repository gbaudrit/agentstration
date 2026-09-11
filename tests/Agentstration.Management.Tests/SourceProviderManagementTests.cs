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
        Assert.AreEqual(ResourceScopeRef.Instance, created.Value.Definition.Extension.ScopeRef);
        Assert.AreEqual("git", created.Value.Definition.ContributionId);
        Assert.AreEqual(1, created.Value.Generation);
        Assert.AreEqual(ProvisioningState.Succeeded, created.Value.Status.ProvisioningState);
        Assert.IsFalse(typeof(SourceProviderProperties).GetProperties().Any(value => value.PropertyType == typeof(Uri)));
    }

    [TestMethod]
    public async Task SourceProviderRejectsMissingAndDescendantExtensionBindings()
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
                    ResourceScopeRef.Tenant(Fixture.TenantId))
            }
        };

        await Assert.ThrowsAsync<ResourceReferenceOutsideScopeException>(() =>
            fixture.SourceProviders.CreateAsync(tenantReference, default));
    }

    [TestMethod]
    public async Task SourceProviderSupportsTenantAndWorkspaceOwnershipWithVisibleExtension()
    {
        using var fixture = new Fixture();
        var tenantScope = ResourceScopeRef.Tenant(Fixture.TenantId);
        var workspaceScope = ResourceScopeRef.Workspace(Fixture.WorkspaceId);
        await fixture.Registrations.CreateAsync(Extension(tenantScope), default);
        var tenantOwned = SourceProvider(tenantScope) with
        {
            Metadata = new ResourceMetadata { Name = "git-tenant" }
        };
        var workspaceOwned = SourceProvider(workspaceScope) with
        {
            Metadata = new ResourceMetadata { Name = "git-workspace" },
            Definition = SourceProvider(workspaceScope).Definition with
            {
                Extension = new("source-extension", tenantScope)
            }
        };

        var tenant = await fixture.SourceProviders.CreateAsync(tenantOwned, default);
        var workspace = await fixture.SourceProviders.CreateAsync(workspaceOwned, default);

        Assert.AreEqual(tenantScope, tenant.Value.ScopeRef);
        Assert.AreEqual(workspaceScope, workspace.Value.ScopeRef);
        Assert.AreEqual(tenantScope, workspace.Value.Definition.Extension.ScopeRef);
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
    public async Task UsageLookupKeepsProvidersWithTheSameNameSeparatedByScope()
    {
        using var fixture = new Fixture();
        var tenantScope = ResourceScopeRef.Tenant(Fixture.TenantId);
        await fixture.Registrations.CreateAsync(Extension(), default);
        await fixture.Registrations.CreateAsync(Extension(tenantScope), default);
        await fixture.SourceProviders.CreateAsync(SourceProvider(), default);
        await fixture.SourceProviders.CreateAsync(SourceProvider(tenantScope), default);
        await fixture.Store.PutExactAsync(tenantScope, new SourceConfigurationResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceConfiguration,
            Metadata = new ResourceMetadata { Name = "catalog", Namespace = new("agentstration") },
            ScopeRef = tenantScope,
            Definition = new SourceConfigurationProperties
            {
                SourceUid = Guid.NewGuid(),
                DisplayName = "Catalog",
                Bindings = [new SourceBindingSelection
                {
                    Name = "git-distribution",
                    TargetKind = SourceKinds.SourceProvider,
                    Target = new("git-local", tenantScope, ResourceNamespace.Default)
                }]
            }
        }, null, true, default);

        var instanceUsages = await fixture.SourceProviders.GetUsagesExactAsync(
            ResourceScopeRef.Instance, ResourceNamespace.Default, "git-local", default);
        var tenantUsages = await fixture.SourceProviders.GetUsagesExactAsync(
            tenantScope, ResourceNamespace.Default, "git-local", default);

        Assert.IsEmpty(instanceUsages);
        Assert.HasCount(1, tenantUsages);
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

    private static ExtensionRegistrationResource Extension(ResourceScopeRef? scopeRef = null) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ExtensionRegistration,
        Metadata = new ResourceMetadata { Name = "source-extension" },
        ScopeRef = scopeRef ?? ResourceScopeRef.Instance,
        Definition = new ExtensionRegistrationProperties
        {
            DisplayName = "Source extension",
            Endpoint = new Uri("http://127.0.0.1:5300"),
            Source = scopeRef?.Kind == ResourceScopeKind.Tenant
                ? ExtensionRegistrationSource.Manual
                : ExtensionRegistrationSource.Configuration
        }
    };

    private static SourceProviderResource SourceProvider(ResourceScopeRef? scopeRef = null) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.SourceProvider,
        Metadata = new ResourceMetadata { Name = "git-local" },
        ScopeRef = scopeRef,
        Definition = new SourceProviderProperties
        {
            DisplayName = "Local Git provider",
            Extension = new("source-extension", scopeRef),
            ContributionId = "git"
        }
    };

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly IDisposable systemScope;

        public Fixture(IEnumerable<IExtensionInspector>? inspectors = null)
        {
            var context = new CurrentRequestContext();
            systemScope = context.PushSystem();
            Store = new MemoryStore();
            var scopes = new TestScopeResolver();
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

    private sealed class TestScopeResolver : IResourceScopeResolver
    {
        private static readonly ResourceScope Instance = new(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
        private static readonly ResourceScope Tenant = new(2, ResourceScopeRef.Tenant(Fixture.TenantId), ResourceScopeKind.Tenant, Fixture.TenantId.ToString("D"), 1);
        private static readonly ResourceScope Workspace = new(3, ResourceScopeRef.Workspace(Fixture.WorkspaceId), ResourceScopeKind.Workspace, Fixture.WorkspaceId.ToString("D"), 2);

        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedResourceScope?>(scopeRef == ResourceScopeRef.Instance
                ? new(Instance, [])
                : scopeRef == Tenant.Ref
                    ? new(Tenant, [Instance])
                    : scopeRef == Workspace.Ref
                        ? new(Workspace, [Tenant, Instance])
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
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values
                .Where(value => value.Key.Address.Kind == kind && value.Value.Value is T)
                .Select(value => new StoredResource<T>((T)value.Value.Value, value.Value.ETag, value.Value.At))
                .Skip(skip)
                .Take(take)
                .ToArray());

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
