using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, scopes,
            NullLogger<RuntimeWorkerOperationGateway>.Instance);

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
            resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, scopes,
            NullLogger<RuntimeWorkerOperationGateway>.Instance);
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

    [TestMethod]
    public async Task ModelInvocationUsesPinnedInstanceModelProfileScope()
    {
        var scopes = new RecordingRequestContextScopes();
        var resolver = new AccessModeAssertingChatClientResolver(scopes, ControlPlaneAccessMode.System);
        var gateway = new RuntimeWorkerOperationGateway(
            resolver, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, scopes,
            NullLogger<RuntimeWorkerOperationGateway>.Instance);
        var agent = Agent() with { ModelProfileScopeRef = ResourceScopeRef.Instance };

        var response = await gateway.InvokeModelAsync(new RuntimeGovernedModelRequest(
            agent,
            [new(RuntimeMessageRole.User, [new RuntimeGovernedModelText("Hello")])],
            [],
            new RuntimeExecutionOptions(),
            Guid.NewGuid(),
            new WorkspaceId(Guid.NewGuid()),
            Guid.NewGuid()), default);

        Assert.AreEqual("OK", Assert.IsInstanceOfType<RuntimeGovernedModelText>(response.Contents.Single()).Text);
        Assert.AreEqual(ResourceScopeRef.Instance, resolver.RequestedScope);
        Assert.AreEqual(ControlPlaneAccessMode.Unavailable, scopes.AccessMode);
    }

    [TestMethod]
    [DataRow(ResourceScopeKind.Instance, ControlPlaneAccessMode.Instance)]
    [DataRow(ResourceScopeKind.Tenant, ControlPlaneAccessMode.TenantAdministration)]
    [DataRow(ResourceScopeKind.Workspace, ControlPlaneAccessMode.Workspace)]
    public void FlowExecutionPreservesLogicalAndOperationalContextsAndRestoresWorkerScope(
        ResourceScopeKind kind, ControlPlaneAccessMode expectedMode)
    {
        var context = new CurrentRequestContext();
        var scope = new FlowRunScope(Guid.NewGuid(), new WorkspaceId(Guid.NewGuid()), Guid.NewGuid());
        scope = scope with
        {
            LogicalScopeRef = kind switch
            {
                ResourceScopeKind.Instance => ResourceScopeRef.Instance,
                ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(scope.TenantId),
                _ => ResourceScopeRef.Workspace(scope.WorkspaceId.Value)
            }
        };
        var execution = new WorkspaceFlowRunExecutionScope(null!, null!, null!, context);
        using var worker = context.PushSystem();
        using (execution.Enter(scope))
        {
            Assert.AreEqual(expectedMode, context.AccessMode);
            Assert.AreEqual(scope.WorkspaceId.Value, context.Current.WorkspaceId);
            Assert.AreEqual(scope.TenantId, context.Current.TenantId);
            Assert.AreEqual(scope.PrincipalId, context.Current.PrincipalId);
            Assert.AreEqual(scope, execution.Current);
        }
        Assert.AreEqual(ControlPlaneAccessMode.System, context.AccessMode);
        Assert.IsNull(execution.Current);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = context.Current; });
    }

    [TestMethod]
    public async Task ToolFailureIsLoggedWithCorrelationAndWithoutArgumentsOrInnerException()
    {
        var logger = new RecordingLogger();
        var scopes = new RecordingRequestContextScopes();
        var gateway = new RuntimeWorkerOperationGateway(null!, new FailingToolPipeline(), null!, null!, null!,
            null!, null!, null!, null!, TimeProvider.System, scopes, logger);
        var tool = new RuntimeExecutionToolMaterial("catalog-tool", ResourceNamespace.Default, "Catalog Tool", null,
            JsonSerializer.SerializeToElement(new { }), null, false, "provider", ResourceNamespace.Default, "external");
        var request = new RuntimeGovernedToolRequest(Agent(), tool, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, Guid.NewGuid(), new WorkspaceId(Guid.NewGuid()), Guid.NewGuid(), "runtime-run",
            JsonSerializer.SerializeToElement(new { credential = "private-tool-argument" }), null);

        var error = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => gateway.InvokeToolAsync(request, default));

        Assert.AreEqual("tool_definition_timed_out", error.Code);
        Assert.AreEqual(LogLevel.Warning, logger.Level);
        Assert.AreEqual("tool_definition_timed_out", logger.Properties["ErrorCode"]);
        Assert.AreEqual("Acquisition exceeded its timeout.", logger.Properties["ErrorDetail"]);
        Assert.AreEqual(request.RunId, logger.Properties["RunId"]);
        Assert.AreEqual(request.ToolCallId, logger.Properties["ToolCallId"]);
        Assert.AreEqual(request.TurnAttemptId, logger.Properties["TurnAttemptId"]);
        Assert.AreEqual(request.ExecutionScopeRef.Value, logger.Properties["ExecutionScopeRef"]);
        Assert.AreEqual(tool.Id, logger.Properties["ToolId"]);
        Assert.IsFalse(logger.Message.Contains("private-tool-argument", StringComparison.Ordinal));
        Assert.IsFalse(logger.Message.Contains("private-inner-detail", StringComparison.Ordinal));
        Assert.IsNull(logger.Exception);
    }

    private sealed class FailingToolPipeline : IToolExecutionPipeline
    {
        public ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<JsonElement?>(new ToolDefinitionInvocationException("tool_definition_timed_out",
                "Acquisition exceeded its timeout.", new InvalidOperationException("private-inner-detail")));
    }

    private sealed class RecordingLogger : ILogger<RuntimeWorkerOperationGateway>
    {
        public LogLevel Level { get; private set; }
        public string Message { get; private set; } = "";
        public Exception? Exception { get; private set; }
        public Dictionary<string, object?> Properties { get; private set; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
            Exception = exception;
            Properties = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
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

    private sealed class AccessModeAssertingChatClientResolver(
        ICurrentRequestContext context,
        ControlPlaneAccessMode expectedMode) : IChatClientResolver
    {
        public ResourceScopeRef? RequestedScope { get; private set; }

        public ValueTask<IChatClient> ResolveAsync(string modelProfileName, CancellationToken cancellationToken = default) =>
            ResolveAsync(ResourceNamespace.Default, modelProfileName, cancellationToken);

        public ValueTask<IChatClient> ResolveAsync(
            ResourceNamespace @namespace,
            string modelProfileName,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(expectedMode, context.AccessMode);
            return ValueTask.FromResult<IChatClient>(new StubChatClient(_ => { }));
        }

        public ValueTask<IChatClient> ResolveAsync(
            ResourceScopeRef profileScopeRef,
            ResourceNamespace @namespace,
            string modelProfileName,
            CancellationToken cancellationToken = default)
        {
            RequestedScope = profileScopeRef;
            return ResolveAsync(@namespace, modelProfileName, cancellationToken);
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
        private ControlPlaneAccessMode mode;
        public bool IsInitialized => mode == ControlPlaneAccessMode.Workspace;
        public ControlPlaneAccessMode AccessMode => mode;
        public RequestContext Current => current ?? throw new InvalidOperationException();

        public IDisposable Push(RequestContext context) => Push(ControlPlaneAccessMode.Workspace, context);

        private IDisposable Push(ControlPlaneAccessMode accessMode, RequestContext? context)
        {
            var previous = current;
            var previousMode = mode;
            current = context;
            mode = accessMode;
            return new Scope(() =>
            {
                current = previous;
                mode = previousMode;
            });
        }

        public IDisposable PushTenant(Guid principalId, Guid tenantId, AuthorizationRestriction? restriction = null) =>
            throw new NotSupportedException();

        public IDisposable PushTenantAdministration(RequestContext operationalContext) => throw new NotSupportedException();

        public IDisposable PushInstance(RequestContext operationalContext) => throw new NotSupportedException();

        public IDisposable PushSystem() => Push(ControlPlaneAccessMode.System, null);

        private sealed class Scope(Action dispose) : IDisposable
        {
            private Action? action = dispose;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
