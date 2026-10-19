using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowToolExecutorTests
{
    [TestMethod]
    [DataRow(ToolProviderType.Mcp)]
    [DataRow(ToolProviderType.Aep)]
    public async Task ExecutorBuildsStableFlowRunIdentitiesAndPassesTheResolvedArguments(ToolProviderType providerType)
    {
        var store = await StoreAsync(providerType);
        var pipeline = new RecordingPipeline();
        var executor = new ManagedFlowToolExecutor(store, pipeline);
        var request = Request(JsonSerializer.SerializeToElement(new { message = "hello" }), 1);

        var output = await executor.ExecuteAsync(request, default);
        await executor.ExecuteAsync(request, default);
        await executor.ExecuteAsync(request with { Attempt = 2 }, default);

        Assert.AreEqual("sent", output.Output?.GetString());
        Assert.AreEqual("notification.send", output.ToolName);
        Assert.AreEqual(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), output.ToolUid);
        Assert.AreEqual(4L, output.ToolGeneration);
        Assert.AreEqual("internal", output.ProviderName);
        Assert.AreEqual(providerType.ToString(), output.ProviderType);
        Assert.AreEqual("notification_send", output.ExternalToolId);
        Assert.HasCount(3, pipeline.Contexts);
        var first = pipeline.Contexts[0];
        Assert.AreEqual(ToolExecutionOwnerKind.FlowRun, first.OwnerKind);
        Assert.AreEqual("flow:run-42:step:notify", first.ToolCallId);
        Assert.AreEqual("flow:run-42:step:notify:attempt:1", first.InvocationId);
        Assert.AreEqual(first.ToolCallId, pipeline.Contexts[2].ToolCallId);
        Assert.AreEqual(first.InvocationId, pipeline.Contexts[1].InvocationId);
        Assert.AreNotEqual(first.InvocationId, pipeline.Contexts[2].InvocationId);
        Assert.AreEqual(Scope.WorkspaceId, first.WorkspaceId);
        Assert.AreEqual(Scope.PrincipalId, first.PrincipalId);
        Assert.AreEqual("notify", first.FlowStepId);
        Assert.AreEqual("hello", first.Arguments?.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task InvalidArgumentsAndApprovalRequirementsFailBeforeTheProviderPipeline()
    {
        var store = await StoreAsync();
        var pipeline = new RecordingPipeline();
        var executor = new ManagedFlowToolExecutor(store, pipeline);

        var invalid = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            executor.ExecuteAsync(Request(JsonSerializer.SerializeToElement(new { message = 42 }), 1), default));
        Assert.AreEqual("tool_argument_type_invalid", invalid.Code);
        Assert.HasCount(0, pipeline.Contexts);

        await store.PutAsync(Tool(requiresApproval: true), null, false, default);
        var approval = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            executor.ExecuteAsync(Request(JsonSerializer.SerializeToElement(new { message = "hello" }), 1), default));
        Assert.AreEqual("tool_approval_required", approval.Code);
        Assert.HasCount(0, pipeline.Contexts);
    }

    [TestMethod]
    public async Task ToolSetRouteExecutesTheExactPinnedToolAndFailsClosedWhenItIsStale()
    {
        var store = await StoreAsync();
        var pipeline = new RecordingPipeline();
        var executor = new ManagedFlowToolExecutor(store, pipeline);
        var request = Request(JsonSerializer.SerializeToElement(new { message = "hello" }), 1) with
        {
            Route = Route()
        };

        _ = await executor.ExecuteAsync(request, default);
        var context = pipeline.Contexts.Single();
        Assert.AreEqual(WorkspaceScope, context.ToolScopeRef);
        Assert.AreEqual(WorkspaceScope, context.ToolProviderScopeRef);
        Assert.AreEqual(Route().ToolUid, context.ToolUid);
        Assert.AreEqual(Route().ProviderUid, context.ToolProviderUid);

        var exception = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            executor.ExecuteAsync(request with { Route = Route() with { ToolGeneration = 3 } }, default));
        Assert.AreEqual("tool_set_member_stale", exception.Code);
    }

    private static readonly FlowRunScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(Scope.WorkspaceId.Value);
    private static readonly Guid ProviderUid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static FlowToolExecutionRequest Request(JsonElement arguments, int attempt) => new(
        Scope,
        "run-42",
        new FlowId("parent"),
        WorkspaceScope,
        "notify",
        attempt,
        "correlation-42",
        new("notification.send"),
        arguments);

    private static ResolvedFlowToolRoute Route() => new(
        new("notification.send"),
        "notifications",
        ResourceNamespace.Default,
        "1.0.0",
        "notification.create",
        "default",
        WorkspaceScope,
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        4,
        "internal",
        ResourceNamespace.Default,
        WorkspaceScope,
        ProviderUid,
        2,
        Tool().Definition.Schema!.Input,
        null,
        false);

    private static async Task<MemoryStore> StoreAsync(ToolProviderType providerType = ToolProviderType.Mcp)
    {
        var store = new MemoryStore();
        await store.PutAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Uid = ProviderUid,
            Generation = 2,
            Metadata = new ResourceMetadata { Name = "internal" },
            ScopeRef = WorkspaceScope,
            Definition = new ToolProviderProperties
            {
                DisplayName = "Test provider",
                ProviderType = providerType,
                Mcp = providerType == ToolProviderType.Mcp ? new McpToolProviderConfiguration { Internal = true } : null,
                Aep = providerType == ToolProviderType.Aep
                    ? new AepToolProviderConfiguration { Extension = new ResourceReference("extension-1") }
                    : null
            }
        }, null, true, default);
        await store.PutAsync(Tool(), null, true, default);
        return store;
    }

    private static ToolResource Tool(bool requiresApproval = false) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.Tool,
        Uid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Generation = 4,
        Metadata = new ResourceMetadata { Name = "notification.send" },
        ScopeRef = WorkspaceScope,
        Definition = new ToolResourceProperties
        {
            DisplayName = "Send notification",
            Enabled = true,
            RequiresApproval = requiresApproval,
            Provider = new("internal", WorkspaceScope),
            ExternalId = "notification_send",
            Discovery = new ToolDiscoveryState
            {
                Available = true,
                FirstSeenAt = DateTimeOffset.UnixEpoch,
                LastSeenAt = DateTimeOffset.UnixEpoch
            },
            Schema = new ToolSchema
            {
                Input = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { message = new { type = "string" } },
                    required = new[] { "message" }
                })
            }
        }
    };

    private sealed class RecordingPipeline : IToolExecutionPipeline
    {
        public List<ToolExecutionContext> Contexts { get; } = [];

        public ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("sent"));
        }
    }

    private sealed class MemoryStore : IResourceStore
    {
        private readonly Dictionary<ResourceKey, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(key, out var entry) && entry.Value is T typed
                ? new StoredResource<T>(typed, entry.ETag, entry.At)
                : null);

        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(new ResourceKey(address.Address.Kind, address.Address.Name, address.Address.Namespace), out var entry)
                && entry.Value is T typed
                && entry.Value.ScopeRef == address.ScopeRef
                    ? new StoredResource<T>(typed, entry.ETag, entry.At)
                    : null);

        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>([]);

        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = new ResourceKey(resource.Kind, resource.Name, resource.Namespace);
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var now = DateTimeOffset.UnixEpoch;
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
