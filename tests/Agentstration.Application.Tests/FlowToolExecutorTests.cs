using Agentstration.Tools;
using Agentstration.ResourceManagement;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowToolExecutorTests
{
    [TestMethod]
    public async Task ExecutorBuildsStableFlowRunIdentitiesAndPassesTheResolvedArguments()
    {
        var store = await StoreAsync();
        var pipeline = new RecordingPipeline();
        var executor = new ManagedFlowToolExecutor(store, pipeline);
        var request = Request(JsonSerializer.SerializeToElement(new { message = "hello" }), 1);

        var output = await executor.ExecuteAsync(request, default);
        await executor.ExecuteAsync(request, default);
        await executor.ExecuteAsync(request with { Attempt = 2 }, default);

        Assert.AreEqual("sent", output?.GetString());
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

    private static readonly FlowRunScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static FlowToolExecutionRequest Request(JsonElement arguments, int attempt) => new(
        Scope,
        "run-42",
        new FlowId("parent"),
        "notify",
        attempt,
        "correlation-42",
        new("notification.send"),
        arguments);

    private static async Task<MemoryStore> StoreAsync()
    {
        var store = new MemoryStore();
        await store.PutAsync(Tool(), null, true, default);
        return store;
    }

    private static ToolResource Tool(bool requiresApproval = false) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Tool,
        Metadata = new ResourceMetadata { Name = "notification.send" },
        Definition = new ToolResourceProperties
        {
            DisplayName = "Send notification",
            Enabled = true,
            RequiresApproval = requiresApproval,
            Provider = new("internal"),
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
