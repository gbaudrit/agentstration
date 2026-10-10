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
            new RuntimeExecutionOptions(),
            tenantId,
            workspaceId,
            principalId), default);

        Assert.AreEqual("OK", Assert.IsInstanceOfType<RuntimeGovernedModelText>(response.Contents.Single()).Text);
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
            return ValueTask.FromResult<IChatClient>(new StubChatClient());
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

        public IDisposable PushSystem() => throw new NotSupportedException();

        private sealed class Scope(Action dispose) : IDisposable
        {
            private Action? action = dispose;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
