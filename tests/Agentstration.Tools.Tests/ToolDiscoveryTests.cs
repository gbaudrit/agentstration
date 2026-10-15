using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolDiscoveryTests
{
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    public async Task RefreshMaterializesSecureDefaultsPreservesGovernanceAndTracksDiffAndAvailability()
    {
        var store = new MemoryStore();
        var provider = Provider();
        await store.PutAsync(provider, null, true, default);
        var discovery = new SequenceDiscovery(
            [Descriptor("a", "A"), Descriptor("b", "B"), Descriptor("c", "C")],
            [Descriptor("a", "A changed"), Descriptor("b", "B"), Descriptor("d", "D")],
            [Descriptor("a", "A changed"), Descriptor("b", "B"), Descriptor("c", "C"), Descriptor("d", "D")]);
        var service = new ToolManagementService(store, new UnusedReferenceResolver(), new TestScopeOperations(), [discovery], new FixedTimeProvider());

        var first = await service.RefreshDiscoveryAsync(provider.Metadata.Name, default);
        var tools = await service.ListToolsAsync(default);
        Assert.AreEqual(3, first.New);
        Assert.IsTrue(tools.All(value => !value.Value.Definition.Enabled && value.Value.Definition.Discovery?.Available == true));

        var b = tools.Single(value => value.Value.Definition.ExternalId == "b");
        await service.SetToolEnabledAsync(b.Value.Metadata.Name, true, b.ETag, default);
        var second = await service.RefreshDiscoveryAsync(provider.Metadata.Name, default);
        tools = await service.ListToolsAsync(default);

        Assert.AreEqual(new ToolDiscoveryDiff(1, 1, 1, 1, 3), second);
        Assert.IsTrue(tools.Single(value => value.Value.Definition.ExternalId == "b").Value.Definition.Enabled);
        Assert.IsFalse(tools.Single(value => value.Value.Definition.ExternalId == "c").Value.Definition.Discovery!.Available);
        Assert.AreEqual("A changed", tools.Single(value => value.Value.Definition.ExternalId == "a").Value.Definition.DisplayName);

        _ = await service.RefreshDiscoveryAsync(provider.Metadata.Name, default);
        tools = await service.ListToolsAsync(default);
        Assert.IsTrue(tools.Single(value => value.Value.Definition.ExternalId == "c").Value.Definition.Discovery!.Available);
    }

    [TestMethod]
    public void ProviderValidationSupportsStdioHttpAndAepWithoutPlaintextEnvironmentValues()
    {
        ToolManagementService.ValidateProvider(Provider());
        ToolManagementService.ValidateProvider(Provider() with { Definition = new ToolProviderProperties { DisplayName = "HTTP", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("https://example.test/mcp") } } });
        ToolManagementService.ValidateProvider(Provider() with { Definition = new ToolProviderProperties { DisplayName = "AEP", ProviderType = ToolProviderType.Aep, Aep = new AepToolProviderConfiguration { Extension = new ResourceReference("extension-test", WorkspaceScope, ResourceNamespace.Default) } } });
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateProvider(Provider() with { Definition = new ToolProviderProperties { DisplayName = "Bad", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration() } }));
    }

    [TestMethod]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    [DataRow(ResourceScopeKind.Workspace)]
    public async Task AepProviderPinsAnEnabledExtensionRegistrationInTheSameScope(ResourceScopeKind scopeKind)
    {
        var scopeRef = Scope(scopeKind);
        var extension = ExtensionRegistration(scopeRef);
        var service = new ToolManagementService(
            new MemoryStore(),
            new TestReferenceResolver(extension),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());

        var stored = await service.PutProviderAsync(AepProvider() with { ScopeRef = scopeRef }, null, true, default);

        Assert.AreEqual(scopeRef, stored.Value.ScopeRef);
        Assert.AreEqual(scopeRef, stored.Value.Definition.Aep!.Extension.ScopeRef);
        Assert.AreEqual(ResourceNamespace.Default, stored.Value.Definition.Aep.Extension.Namespace);
        Assert.AreEqual("utilities-extension", stored.Value.Definition.Aep.Extension.Name);
    }

    [TestMethod]
    public async Task AepProviderRejectsAnAncestorOrDisabledExtensionRegistration()
    {
        var ancestor = new ToolManagementService(
            new MemoryStore(),
            new TestReferenceResolver(ExtensionRegistration(ResourceScopeRef.Instance)),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());
        var disabled = new ToolManagementService(
            new MemoryStore(),
            new TestReferenceResolver(ExtensionRegistration(WorkspaceScope) with
            {
                Definition = ExtensionRegistration(WorkspaceScope).Definition with { Enabled = false }
            }),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());

        var ancestorException = await Assert.ThrowsAsync<ToolResourceValidationException>(async () =>
            await ancestor.PutProviderAsync(AepProvider(), null, true, default));
        var disabledException = await Assert.ThrowsAsync<ToolResourceValidationException>(async () =>
            await disabled.PutProviderAsync(AepProvider(), null, true, default));

        StringAssert.Contains(ancestorException.Message, "same scope");
        StringAssert.Contains(disabledException.Message, "disabled");
    }

    [TestMethod]
    public async Task AepProviderRejectsAnAmbiguousUnscopedRegistrationReference()
    {
        var service = new ToolManagementService(
            new MemoryStore(),
            new AmbiguousReferenceResolver(),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());

        var exception = await Assert.ThrowsAsync<ToolResourceValidationException>(async () =>
            await service.PutProviderAsync(AepProvider(), null, true, default));

        StringAssert.Contains(exception.Message, "more than one ownership scope");
    }

    [TestMethod]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    public async Task NonAepProvidersRemainWorkspaceOwned(ResourceScopeKind scopeKind)
    {
        var service = new ToolManagementService(
            new MemoryStore(),
            new UnusedReferenceResolver(),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());

        var exception = await Assert.ThrowsAsync<ToolResourceValidationException>(async () =>
            await service.PutProviderAsync(Provider() with { ScopeRef = Scope(scopeKind) }, null, true, default));

        StringAssert.Contains(exception.Message, "Only AEP ToolProviders");
    }

    [TestMethod]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    public async Task BroadScopeToolsCanOnlyBeMaterializedByAepDiscovery(ResourceScopeKind scopeKind)
    {
        var service = new ToolManagementService(
            new MemoryStore(),
            new UnusedReferenceResolver(),
            new TestScopeOperations(),
            [new SequenceDiscovery([])],
            new FixedTimeProvider());
        var tool = new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "manual-tool" },
            ScopeRef = Scope(scopeKind),
            Definition = new ToolResourceProperties
            {
                DisplayName = "Manual tool",
                ToolType = new ToolTypeReference("extension.test", "manual")
            }
        };

        var exception = await Assert.ThrowsAsync<ToolResourceValidationException>(async () =>
            await service.PutToolAsync(tool, null, true, default));

        StringAssert.Contains(exception.Message, "AEP discovery");
    }

    private static ToolProviderResource Provider() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = "test" },
        ScopeRef = WorkspaceScope,
        Definition = new ToolProviderProperties { DisplayName = "Test", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Command = "test-mcp", EnvironmentReferences = new Dictionary<string, string> { ["TOKEN"] = "Secrets:Mcp:Token" } } }
    };

    private static ToolProviderResource AepProvider() => Provider() with
    {
        ScopeRef = WorkspaceScope,
        Definition = new ToolProviderProperties
        {
            DisplayName = "Utilities",
            ProviderType = ToolProviderType.Aep,
            Aep = new AepToolProviderConfiguration { Extension = new ResourceReference("utilities-extension") }
        }
    };

    private static ExtensionRegistrationResource ExtensionRegistration(ResourceScopeRef scopeRef) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ExtensionKinds.ExtensionRegistration,
        Metadata = new ResourceMetadata { Name = "utilities-extension" },
        ScopeRef = scopeRef,
        Definition = new ExtensionRegistrationProperties
        {
            DisplayName = "Utilities",
            Endpoint = new Uri("http://utilities/"),
            Enabled = true,
            ExpectedExtensionId = "Agentstration.Extensions.Utilities"
        }
    };

    private static ResourceScopeRef Scope(ResourceScopeKind kind) => kind switch
    {
        ResourceScopeKind.Instance => ResourceScopeRef.Instance,
        ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        ResourceScopeKind.Workspace => WorkspaceScope,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static DiscoveredToolDescriptor Descriptor(string id, string display) => new(id, display, $"{display} description", JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "string" } } }), null, new Dictionary<string, JsonElement>());

    private sealed class SequenceDiscovery(params DiscoveredToolDescriptor[][] results) : IToolProviderDiscovery
    {
        private int index;
        public bool Supports(ToolProviderType providerType) => true;
        public Task<ToolProviderDiscoveryResult> DiscoverAsync(ToolProviderResource provider, CancellationToken cancellationToken)
        {
            var value = results[Math.Min(index++, results.Length - 1)];
            return Task.FromResult(new ToolProviderDiscoveryResult(value, new Dictionary<string, bool> { ["tools"] = true }, new Dictionary<string, string>()));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMinutes(Interlocked.Increment(ref ticks));
    }

    private sealed class MemoryStore : IResourceStore
    {
        private readonly Dictionary<ResourceKey, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.TryGetValue(key, out var entry) && entry.Value is T typed ? new StoredResource<T>(typed, entry.ETag, entry.At) : null);
        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource => GetAsync<T>(new ResourceKey(address.Address.Kind, address.Address.Name, address.Address.Namespace), cancellationToken);
        public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Values.Where(entry => entry.Value is T && entry.Value.Kind == kind && entry.Value.ScopeRef == scopeRef).Select(entry => new StoredResource<T>((T)entry.Value, entry.ETag, entry.At)).Skip(skip).Take(take).ToArray());
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Values.Where(entry => entry.Value is T && entry.Value.Kind == kind).Select(entry => new StoredResource<T>((T)entry.Value, entry.ETag, entry.At)).Skip(skip).Take(take).ToArray());
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var etag = $"\"{Interlocked.Increment(ref version)}\""; var at = DateTimeOffset.UtcNow; values[new(resource.Kind, resource.Metadata.Name)] = (resource, etag, at); return Task.FromResult(new StoredResource<T>(resource, etag, at));
        }
        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, ifMatch, ifNoneMatch, cancellationToken);
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) { values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class UnusedReferenceResolver : IResourceReferenceResolver
    {
        public Task<StoredResource<T>?> ResolveAsync<T>(ResourceReference reference, ResourceNamespace ownerNamespace, string kind, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
    }

    private sealed class TestReferenceResolver(ExtensionRegistrationResource extension) : IResourceReferenceResolver
    {
        public Task<StoredResource<T>?> ResolveAsync<T>(ResourceReference reference, ResourceNamespace ownerNamespace, string kind, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken) where T : Resource
        {
            var result = extension is T typed && kind == ExtensionKinds.ExtensionRegistration
                ? new StoredResource<T>(typed, "test", DateTimeOffset.UnixEpoch)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class AmbiguousReferenceResolver : IResourceReferenceResolver
    {
        public Task<StoredResource<T>?> ResolveAsync<T>(ResourceReference reference, ResourceNamespace ownerNamespace, string kind, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken) where T : Resource =>
            throw new ResourceReferenceAmbiguousException(reference, kind);
    }

    private sealed class TestScopeOperations : IResourceScopeOperations
    {
        public ResourceScopeRef DefaultScopeRef(string kind) => WorkspaceScope;
        public ResourceScopeRef TargetScopeRef(ResourceScopeKind kind) => WorkspaceScope;
        public Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(string kind, string permission, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTarget>>([]);
        public Task<T> WriteAsync<T>(Resource resource, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
        public Task<T> WriteAsync<T>(string kind, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
