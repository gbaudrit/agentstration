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

        var first = await service.RefreshDiscoveryAsync(provider.Id, default);
        var tools = await service.ListToolsAsync("default", default);
        Assert.AreEqual(3, first.New);
        Assert.IsTrue(tools.All(value => !value.Value.Properties.Enabled && value.Value.Properties.Discovery?.Available == true));

        var b = tools.Single(value => value.Value.Properties.ExternalId == "b");
        await service.SetToolEnabledAsync(b.Value.Id, true, b.ETag, default);
        var second = await service.RefreshDiscoveryAsync(provider.Id, default);
        tools = await service.ListToolsAsync("default", default);

        Assert.AreEqual(new ToolDiscoveryDiff(1, 1, 1, 1, 3), second);
        Assert.IsTrue(tools.Single(value => value.Value.Properties.ExternalId == "b").Value.Properties.Enabled);
        Assert.IsFalse(tools.Single(value => value.Value.Properties.ExternalId == "c").Value.Properties.Discovery!.Available);
        Assert.AreEqual("A changed", tools.Single(value => value.Value.Properties.ExternalId == "a").Value.Properties.DisplayName);

        _ = await service.RefreshDiscoveryAsync(provider.Id, default);
        tools = await service.ListToolsAsync("default", default);
        Assert.IsTrue(tools.Single(value => value.Value.Properties.ExternalId == "c").Value.Properties.Discovery!.Available);
    }

    [TestMethod]
    public void ProviderValidationSupportsStdioHttpAndAepWithoutPlaintextEnvironmentValues()
    {
        ToolManagementService.ValidateProvider(Provider());
        ToolManagementService.ValidateProvider(Provider() with { Properties = new ToolProviderProperties { DisplayName = "HTTP", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("https://example.test/mcp") } } });
        ToolManagementService.ValidateProvider(Provider() with { Properties = new ToolProviderProperties { DisplayName = "AEP", ProviderType = ToolProviderType.Aep, Aep = new AepToolProviderConfiguration { ExtensionId = "extension.test" } } });
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateProvider(Provider() with { Properties = new ToolProviderProperties { DisplayName = "Bad", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration() } }));
    }

    private static ToolProviderResource Provider() => new()
    {
        Id = ToolManagementService.ToolProviderId("default", "test"), Name = "test", Type = AgentstrationResourceTypes.ToolProviders,
        ApiVersion = ManagementApiVersions.V20260801, ResourceGroup = "default",
        Properties = new ToolProviderProperties { DisplayName = "Test", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Command = "test-mcp", EnvironmentReferences = new Dictionary<string, string> { ["TOKEN"] = "Secrets:Mcp:Token" } } }
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
        private readonly Dictionary<string, (Resource Value, string ETag, DateTimeOffset At)> values = new(StringComparer.Ordinal);
        private long version;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(string resourceId, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.TryGetValue(resourceId, out var entry) && entry.Value is T typed ? new StoredResource<T>(typed, entry.ETag, entry.At) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string resourceType, string? resourceGroup, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Values.Where(entry => entry.Value is T && entry.Value.Type == resourceType && (resourceGroup is null || entry.Value.ResourceGroup == resourceGroup)).Select(entry => new StoredResource<T>((T)entry.Value, entry.ETag, entry.At)).Skip(skip).Take(take).ToArray());
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var etag = $"\"{Interlocked.Increment(ref version)}\""; var at = DateTimeOffset.UtcNow; values[resource.Id] = (resource, etag, at); return Task.FromResult(new StoredResource<T>(resource, etag, at));
        }
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(string resourceId, string? ifMatch, CancellationToken cancellationToken) { values.Remove(resourceId); return Task.CompletedTask; }
    }
}
