using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolExecutionHookManagementTests
{
    [TestMethod]
    public async Task CrudPreservesNamespaceGenerationAndValidatedDefinition()
    {
        var store = new MemoryStore();
        var service = new ToolExecutionHookManagementService(store);
        var resource = Hook();

        var created = await service.CreateAsync(resource, default);
        var listed = await service.ListAsync(default);
        var updated = await service.PutAsync(
            resource.Namespace,
            resource.Name,
            resource.Definition with { Enabled = false, Order = 20 },
            created.ETag,
            default);

        Assert.AreEqual(1L, created.Value.Generation);
        Assert.AreEqual(resource.Namespace, created.Value.Namespace);
        Assert.HasCount(1, listed);
        Assert.AreEqual(2L, updated.Value.Generation);
        Assert.IsFalse(updated.Value.Definition.Enabled);
        Assert.AreEqual(20, updated.Value.Definition.Order);

        await service.DeleteAsync(resource.Namespace, resource.Name, updated.ETag, default);
        Assert.IsNull(await service.GetAsync(resource.Namespace, resource.Name, default));
    }

    [TestMethod]
    public void ValidationRejectsUnknownHandlersInvalidSelectorsAndInvalidDenyConfiguration()
    {
        var resource = Hook();

        Assert.ThrowsExactly<ToolExecutionHookValidationException>(() =>
            ToolExecutionHookManagementService.Validate(resource with
            {
                Definition = resource.Definition with { Handler = "arbitrary-dotnet-type" }
            }));
        Assert.ThrowsExactly<ToolExecutionHookValidationException>(() =>
            ToolExecutionHookManagementService.Validate(resource with
            {
                Definition = resource.Definition with
                {
                    Selector = new ToolExecutionHookSelector { Tools = ["lookup", "lookup"] }
                }
            }));
        Assert.ThrowsExactly<ToolExecutionHookValidationException>(() =>
            ToolExecutionHookManagementService.Validate(resource with
            {
                Definition = resource.Definition with
                {
                    Configuration = Configuration(("code", JsonSerializer.SerializeToElement("invalid code")), ("message", JsonSerializer.SerializeToElement("Denied")))
                }
            }));
        Assert.ThrowsExactly<ToolExecutionHookValidationException>(() =>
            ToolExecutionHookManagementService.Validate(resource with
            {
                Definition = resource.Definition with
                {
                    Configuration = Configuration(("code", JsonSerializer.SerializeToElement("denied")), ("message", JsonSerializer.SerializeToElement("Denied")), ("script", JsonSerializer.SerializeToElement("run-me")))
                }
            }));
    }

    [TestMethod]
    public void ManifestRoundTripPreservesHookSelectorsAndConfiguration()
    {
        var resource = Hook();

        var json = ResourceManifestSerializer.ToJson(resource);
        var yaml = ResourceManifestSerializer.ToYaml(resource);
        var fromJson = ResourceManifestSerializer.FromJson<ToolExecutionHookResource>(json);
        var fromYaml = ResourceManifestSerializer.FromYaml<ToolExecutionHookResource>(yaml);

        Assert.AreEqual(resource.Definition.Handler, fromJson.Definition.Handler);
        Assert.AreEqual("lookup", fromJson.Definition.Selector.Tools[0]);
        Assert.AreEqual("managed_hook_denied", fromYaml.Definition.Configuration["code"].GetString());
    }

    private static ToolExecutionHookResource Hook() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolExecutionHook,
        Metadata = new ResourceMetadata
        {
            Name = "block-lookup",
            Namespace = new ResourceNamespace("local.governance")
        },
        Definition = new ToolExecutionHookProperties
        {
            DisplayName = "Block lookup",
            Handler = ToolExecutionHookHandlers.Deny,
            Order = 10,
            Selector = new ToolExecutionHookSelector
            {
                Tools = ["lookup"],
                Providers = ["provider"],
                Agents = ["agent"]
            },
            Configuration = Configuration(
                ("code", JsonSerializer.SerializeToElement("managed_hook_denied")),
                ("message", JsonSerializer.SerializeToElement("This Tool is blocked.")))
        }
    };

    private static IReadOnlyDictionary<string, JsonElement> Configuration(params (string Key, JsonElement Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

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
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Values
                .Where(entry => entry.Value is T && entry.Value.Kind == kind)
                .Select(entry => new StoredResource<T>((T)entry.Value, entry.ETag, entry.At))
                .Skip(skip)
                .Take(take)
                .ToArray());

        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = new ResourceKey(resource.Kind, resource.Name, resource.Namespace);
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var now = DateTimeOffset.UtcNow;
            values[key] = (resource, etag, now);
            return Task.FromResult(new StoredResource<T>(resource, etag, now));
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
