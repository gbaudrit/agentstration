using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolDiscoveryTests
{
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
        var service = new ToolManagementService(store, [discovery], new FixedTimeProvider());

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
        ToolManagementService.ValidateProvider(Provider() with { Definition = new ToolProviderProperties { DisplayName = "AEP", ProviderType = ToolProviderType.Aep, Aep = new AepToolProviderConfiguration { ExtensionId = "extension.test" } } });
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateProvider(Provider() with { Definition = new ToolProviderProperties { DisplayName = "Bad", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration() } }));
    }

    private static ToolProviderResource Provider() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = "test" },
        Definition = new ToolProviderProperties { DisplayName = "Test", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Command = "test-mcp", EnvironmentReferences = new Dictionary<string, string> { ["TOKEN"] = "Secrets:Mcp:Token" } } }
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

    private sealed class MemoryStore : IControlPlaneStore
    {
        private readonly Dictionary<ResourceKey, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.TryGetValue(key, out var entry) && entry.Value is T typed ? new StoredResource<T>(typed, entry.ETag, entry.At) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Values.Where(entry => entry.Value is T && entry.Value.Kind == kind).Select(entry => new StoredResource<T>((T)entry.Value, entry.ETag, entry.At)).Skip(skip).Take(take).ToArray());
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var etag = $"\"{Interlocked.Increment(ref version)}\""; var at = DateTimeOffset.UtcNow; values[new(resource.Kind, resource.Metadata.Name)] = (resource, etag, at); return Task.FromResult(new StoredResource<T>(resource, etag, at));
        }
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) { values.Remove(key); return Task.CompletedTask; }
    }
}
