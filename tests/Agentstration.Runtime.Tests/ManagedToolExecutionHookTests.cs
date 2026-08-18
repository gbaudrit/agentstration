using System.Text.Json;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class ManagedToolExecutionHookTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly WorkspaceId Workspace = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [TestMethod]
    public async Task MatchingManagedDenyHookStopsProviderAndPreservesConfiguredDiagnostic()
    {
        var providerCalls = 0;
        var lifecycle = new RecordingSink();
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [],
            new ManagementToolExecutionHookResolver(new HookResourceStore([Hook()])),
            [lifecycle],
            TimeProvider.System);

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionDeniedException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("managed_hook_denied", exception.Code);
        Assert.AreEqual("Managed policy blocked this call.", exception.Message);
        Assert.StartsWith("managed:", exception.HookId, StringComparison.Ordinal);
        Assert.AreEqual(0, providerCalls);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Denied, failed.FailureKind);
        Assert.AreEqual("managed_hook_denied", failed.ErrorCode);
    }

    [TestMethod]
    public async Task ManagedHooksAreFilteredByWorkspaceTenantAndSelectors()
    {
        var providerCalls = 0;
        var resources = new[]
        {
            Hook() with { WorkspaceId = Guid.NewGuid() },
            Hook() with { TenantId = Guid.NewGuid() },
            Hook() with
            {
                Metadata = new ResourceMetadata { Name = "other-tool" },
                Definition = Hook().Definition with
                {
                    Selector = Hook().Definition.Selector with { Tools = ["other"] }
                }
            },
            Hook() with
            {
                Metadata = new ResourceMetadata { Name = "disabled" },
                Definition = Hook().Definition with { Enabled = false }
            }
        };
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [],
            new ManagementToolExecutionHookResolver(new HookResourceStore(resources)),
            [],
            TimeProvider.System);

        await pipeline.ExecuteAsync(Context(), default);
        await pipeline.ExecuteAsync(Context() with { WorkspaceId = null }, default);

        Assert.AreEqual(2, providerCalls);
    }

    [TestMethod]
    public async Task ResolverFailureIsClassifiedAsHookFailureBeforeProviderInvocation()
    {
        var providerCalls = 0;
        var lifecycle = new RecordingSink();
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [],
            new ManagementToolExecutionHookResolver(new FailingHookResourceStore()),
            [lifecycle],
            TimeProvider.System);

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionHookException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("hook-resolver", exception.HookId);
        Assert.AreEqual("resolve", exception.Phase);
        Assert.AreEqual(0, providerCalls);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Hook, failed.FailureKind);
    }

    private static ToolExecutionHookResource Hook() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolExecutionHook,
        Metadata = new ResourceMetadata { Name = "managed-deny" },
        TenantId = Tenant,
        WorkspaceId = Workspace.Value,
        Generation = 1,
        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
        Definition = new ToolExecutionHookProperties
        {
            DisplayName = "Managed deny",
            Handler = ToolExecutionHookHandlers.Deny,
            Order = 10,
            Selector = new ToolExecutionHookSelector
            {
                Tools = ["lookup"],
                Providers = ["provider"],
                Agents = ["agent"]
            },
            Configuration = new Dictionary<string, JsonElement>
            {
                ["code"] = JsonSerializer.SerializeToElement("managed_hook_denied"),
                ["message"] = JsonSerializer.SerializeToElement("Managed policy blocked this call.")
            }
        }
    };

    private static ToolExecutionContext Context() => new()
    {
        OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
        ToolCallId = "logical-call",
        InvocationId = "attempt-1",
        ToolId = "lookup",
        ToolName = "lookup",
        ToolProviderId = "provider",
        TenantId = Tenant,
        WorkspaceId = Workspace,
        AgentId = "agent",
        RunId = "run-1"
    };

    private sealed class RecordingSink : IToolExecutionEventSink
    {
        public List<ToolExecutionLifecycleEvent> Events { get; } = [];

        public ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(executionEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegateInvoker(
        Func<ToolExecutionContext, CancellationToken, ValueTask<JsonElement?>> invoke) : IToolInvoker
    {
        public ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            invoke(context, cancellationToken);
    }

    private sealed class HookResourceStore(IReadOnlyList<ToolExecutionHookResource> resources) : IControlPlaneStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(resources
                .Where(resource => resource.Kind == kind && resource is T)
                .Skip(skip)
                .Take(take)
                .Select(resource => new StoredResource<T>((T)(Resource)resource, "etag", DateTimeOffset.UnixEpoch))
                .ToArray());
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingHookResourceStore : IControlPlaneStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromException<IReadOnlyList<StoredResource<T>>>(new InvalidOperationException("management store unavailable"));
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
