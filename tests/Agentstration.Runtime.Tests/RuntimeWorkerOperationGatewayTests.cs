using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Runtime;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeWorkerOperationGatewayTests
{
    [TestMethod]
    public async Task ModelInvocationRunsInsideAssignedWorkspaceContext()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var principalId = Guid.NewGuid();
        var scopes = new RecordingRequestContextScopes();
        var resolver = new ContextAssertingChatClientResolver(scopes, tenantId, workspaceId.Value, principalId);
        var gateway = new RuntimeWorkerOperationGateway(
            resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, scopes);

        var response = await gateway.InvokeModelAsync(new RuntimeGovernedModelRequest(
            Agent(),
            [new(RuntimeMessageRole.User, [new RuntimeGovernedModelText("Hello")])],
            [new("handoff_to_reviewer", "Transfer to the reviewer.",
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }))],
            new RuntimeExecutionOptions(),
            tenantId,
            workspaceId,
            principalId), default);

        Assert.AreEqual("OK", Assert.IsInstanceOfType<RuntimeGovernedModelText>(response.Contents.Single()).Text);
        Assert.AreEqual("handoff_to_reviewer",
            Assert.IsInstanceOfType<AIFunctionDeclaration>(resolver.LastOptions?.Tools?.Single()).Name);
        Assert.AreEqual(ControlPlaneAccessMode.Unavailable, scopes.AccessMode);
    }

    [TestMethod]
    public async Task ModelStreamingKeepsAssignedWorkspaceContextUntilEnumerationCompletes()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var principalId = Guid.NewGuid();
        var scopes = new RecordingRequestContextScopes();
        var resolver = new ContextAssertingChatClientResolver(scopes, tenantId, workspaceId.Value, principalId);
        var gateway = new RuntimeWorkerOperationGateway(
            resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, scopes);
        var updates = new List<RuntimeGovernedModelUpdate>();

        await foreach (var update in gateway.StreamModelAsync(new RuntimeGovernedModelRequest(
            Agent(),
            [new(RuntimeMessageRole.User, [new RuntimeGovernedModelText("Hello")])],
            [],
            new RuntimeExecutionOptions(),
            tenantId,
            workspaceId,
            principalId), default))
        {
            Assert.AreEqual(ControlPlaneAccessMode.Workspace, scopes.AccessMode);
            updates.Add(update);
        }

        Assert.HasCount(2, updates);
        Assert.AreEqual("O", Assert.IsInstanceOfType<RuntimeGovernedModelText>(updates[0].Contents.Single()).Text);
        Assert.AreEqual(2, updates[1].InputTokens);
        Assert.AreEqual(1, updates[1].OutputTokens);
        Assert.AreEqual(ControlPlaneAccessMode.Unavailable, scopes.AccessMode);
    }

    private static RuntimeExecutionAgentMaterial Agent() => new(
        "agent-material", "agent", Guid.NewGuid(), "agent", 1, "revision", "hash", "handler",
        "Agent", "Agent", "Instructions", "default-reasoning", ResourceNamespace.Default, []);

    private sealed class ContextAssertingChatClientResolver(
        ICurrentRequestContext context,
        Guid tenantId,
        Guid workspaceId,
        Guid principalId) : IChatClientResolver
    {
        public ChatOptions? LastOptions { get; private set; }

        public ValueTask<IChatClient> ResolveAsync(string modelProfileName, CancellationToken cancellationToken = default) =>
            ResolveAsync(ResourceNamespace.Default, modelProfileName, cancellationToken);

        public ValueTask<IChatClient> ResolveAsync(
            ResourceNamespace @namespace,
            string modelProfileName,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(ControlPlaneAccessMode.Workspace, context.AccessMode);
            Assert.AreEqual(tenantId, context.Current.TenantId);
            Assert.AreEqual(workspaceId, context.Current.WorkspaceId);
            Assert.AreEqual(principalId, context.Current.PrincipalId);
            return ValueTask.FromResult<IChatClient>(new StubChatClient(options => LastOptions = options));
        }
    }

    private sealed class StubChatClient(Action<ChatOptions?> captureOptions) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            captureOptions(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            captureOptions(options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("O")])
            {
                ModelId = "model"
            };
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1 })])
            {
                ModelId = "model",
                FinishReason = new("stop")
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingRequestContextScopes : ICurrentRequestContext, IRequestContextScopeFactory
    {
        private RequestContext? current;
        public bool IsInitialized => current is not null;
        public ControlPlaneAccessMode AccessMode => current is null
            ? ControlPlaneAccessMode.Unavailable
            : ControlPlaneAccessMode.Workspace;
        public RequestContext Current => current ?? throw new InvalidOperationException();

        public IDisposable Push(RequestContext context)
        {
            var previous = current;
            current = context;
            return new Scope(() => current = previous);
        }

        public IDisposable PushTenant(Guid principalId, Guid tenantId, AuthorizationRestriction? restriction = null) =>
            throw new NotSupportedException();

        public IDisposable PushTenantAdministration(RequestContext operationalContext) => throw new NotSupportedException();

        public IDisposable PushInstance(RequestContext operationalContext) => throw new NotSupportedException();

        public IDisposable PushSystem() => throw new NotSupportedException();

        private sealed class Scope(Action dispose) : IDisposable
        {
            private Action? action = dispose;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
