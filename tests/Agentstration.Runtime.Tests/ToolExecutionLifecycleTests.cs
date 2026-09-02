using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class ToolExecutionLifecycleTests
{
    [TestMethod]
    public async Task PipelinePublishesStartedAndCompletedWithoutPersistingProviderResult()
    {
        var events = new RecordingSink();
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { secret = "result" }))),
            [events],
            new AdvancingTimeProvider());

        var result = await pipeline.ExecuteAsync(Context(), default);

        Assert.IsNotNull(result);
        Assert.HasCount(3, events.Events);
        Assert.IsInstanceOfType<ToolExecutionStarted>(events.Events[0]);
        var governance = Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(events.Events[1]);
        Assert.IsEmpty(governance.Evaluations);
        var completed = Assert.IsInstanceOfType<ToolExecutionCompleted>(events.Events[2]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), completed.Duration);
    }

    [TestMethod]
    public async Task PipelinePublishesFailureAndPreservesProviderException()
    {
        var events = new RecordingSink();
        var expected = new ProviderDiagnosticException("provider diagnostic");
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromException<JsonElement?>(expected)),
            [events],
            new AdvancingTimeProvider());

        var actual = await Assert.ThrowsExactlyAsync<ProviderDiagnosticException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreSame(expected, actual);
        Assert.HasCount(3, events.Events);
        Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(events.Events[1]);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(events.Events[2]);
        Assert.IsFalse(failed.Cancelled);
        Assert.AreEqual(typeof(ProviderDiagnosticException).FullName, failed.ErrorType);
        Assert.AreEqual("provider diagnostic", failed.ErrorMessage);
    }

    [TestMethod]
    public async Task TerminalProjectionFailureDoesNotMaskProviderException()
    {
        var expected = new ProviderDiagnosticException("provider diagnostic");
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromException<JsonElement?>(expected)),
            [new FailingTerminalSink()],
            new AdvancingTimeProvider());

        var actual = await Assert.ThrowsExactlyAsync<ProviderDiagnosticException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreSame(expected, actual);
        Assert.AreEqual("projection failed", actual.Data["Agentstration.ToolExecutionProjectionError"]);
    }

    [TestMethod]
    public async Task PipelinePublishesCancellationAndPropagatesIt()
    {
        var events = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, token) => ValueTask.FromCanceled<JsonElement?>(token)),
            [events],
            new AdvancingTimeProvider());

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => pipeline.ExecuteAsync(Context(), cancellation.Token).AsTask());

        Assert.HasCount(3, events.Events);
        Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(events.Events[1]);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(events.Events[2]);
        Assert.IsTrue(failed.Cancelled);
    }

    [TestMethod]
    public async Task HooksRunInStableOrderAndUnwindAfterInvocation()
    {
        var calls = new List<string>();
        var hooks = new IToolExecutionHook[]
        {
            SequenceHook("z", 10, calls),
            SequenceHook("b", 0, calls),
            SequenceHook("a", 0, calls)
        };
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                calls.Add("invoke");
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            hooks,
            [],
            new AdvancingTimeProvider());

        await pipeline.ExecuteAsync(Context(), default);

        CollectionAssert.AreEqual(
            new[] { "before:a", "before:b", "before:z", "invoke", "after:z:succeeded", "after:b:succeeded", "after:a:succeeded" },
            calls);
    }

    [TestMethod]
    public async Task DenyingHookShortCircuitsProviderAndIsProjectedDistinctly()
    {
        var providerCalls = 0;
        var calls = new List<string>();
        var lifecycle = new RecordingSink();
        var store = new ProjectionRuntimeRunStore(Run());
        var hooks = new IToolExecutionHook[]
        {
            SequenceHook("audit", 0, calls),
            new DelegateHook(
                "deny-local",
                10,
                (_, _) =>
                {
                    calls.Add("before:deny-local");
                    return ValueTask.FromResult(ToolExecutionHookDecision.Deny("local_policy_denied", "Local policy denied this Tool call."));
                },
                (_, outcome, _) =>
                {
                    calls.Add($"after:deny-local:{outcome.Kind.ToString().ToLowerInvariant()}");
                    return ValueTask.CompletedTask;
                }),
            SequenceHook("never", 20, calls)
        };
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            hooks,
            [lifecycle, new RuntimeToolExecutionEventSink(new RuntimeRunStateManager(store, TimeProvider.System))],
            new AdvancingTimeProvider());

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionDeniedException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("deny-local", exception.HookId);
        Assert.AreEqual("local_policy_denied", exception.Code);
        Assert.AreEqual(0, providerCalls);
        CollectionAssert.AreEqual(
            new[] { "before:audit", "before:deny-local", "after:deny-local:denied", "after:audit:denied" },
            calls);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Denied, failed.FailureKind);
        Assert.AreEqual("local_policy_denied", failed.ErrorCode);
        var governance = Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(lifecycle.Events[^2]);
        Assert.HasCount(2, governance.Evaluations);
        Assert.AreEqual(ToolExecutionHookEvaluationKind.Allowed, governance.Evaluations[0].Decision);
        Assert.AreEqual(ToolExecutionHookEvaluationKind.Denied, governance.Evaluations[1].Decision);
        Assert.AreEqual("local_policy_denied", governance.Evaluations[1].Code);
        var projected = store.Current.Value.Status.ToolCalls[0];
        Assert.AreEqual(ToolExecutionFailureKind.Denied, projected.FailureKind);
        Assert.AreEqual("local_policy_denied", projected.ErrorCode);
    }

    [TestMethod]
    public async Task AfterHookFailureFailsGovernedCallAfterProviderEffect()
    {
        var lifecycle = new RecordingSink();
        var providerCalls = 0;
        var terminalCalls = 0;
        var failingHook = new DelegateHook(
            "terminal-failure",
            10,
            null,
            (_, _, _) => ValueTask.FromException(new InvalidOperationException("terminal hook failed")));
        var observingHook = new DelegateHook(
            "terminal-observer",
            0,
            null,
            (_, _, _) =>
            {
                terminalCalls++;
                return ValueTask.CompletedTask;
            });
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [failingHook, observingHook],
            [lifecycle],
            new AdvancingTimeProvider());

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionHookException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("terminal-failure", exception.HookId);
        Assert.AreEqual("after-invoke", exception.Phase);
        Assert.AreEqual(1, providerCalls);
        Assert.AreEqual(1, terminalCalls);
        Assert.HasCount(3, lifecycle.Events);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Hook, failed.FailureKind);
        Assert.AreEqual("tool_execution_hook_failed", failed.ErrorCode);
    }

    [TestMethod]
    public async Task BeforeHookFailureIsClassifiedAndShortCircuitsProvider()
    {
        var providerCalls = 0;
        var lifecycle = new RecordingSink();
        var hook = new DelegateHook(
            "broken-before",
            0,
            (_, _) => ValueTask.FromException<ToolExecutionHookDecision>(new InvalidOperationException("hook diagnostic")),
            null);
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [hook],
            [lifecycle],
            new AdvancingTimeProvider());

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionHookException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("broken-before", exception.HookId);
        Assert.AreEqual("before-invoke", exception.Phase);
        Assert.AreEqual("hook diagnostic", exception.InnerException?.Message);
        Assert.AreEqual(0, providerCalls);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Hook, failed.FailureKind);
    }

    [TestMethod]
    public async Task GovernanceTraceIsPersistedBeforeProviderInvocationWithStableHookIdentity()
    {
        var lifecycle = new RecordingSink();
        var providerCalls = 0;
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(lifecycle.Events[^1]);
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [SequenceHook("audit", 20, []), SequenceHook("guard", 10, [])],
            [lifecycle],
            new AdvancingTimeProvider());

        await pipeline.ExecuteAsync(Context(), default);

        Assert.AreEqual(1, providerCalls);
        var governance = Assert.IsInstanceOfType<ToolExecutionGovernanceEvaluated>(lifecycle.Events[1]);
        CollectionAssert.AreEqual(new[] { "guard", "audit" }, governance.Evaluations.Select(item => item.Hook.Id).ToArray());
        Assert.IsTrue(governance.Evaluations.All(item => item.Hook.Source == ToolExecutionHookSource.Local));
        Assert.IsTrue(governance.Evaluations.All(item => item.Decision == ToolExecutionHookEvaluationKind.Allowed));
    }

    [TestMethod]
    public async Task GovernanceProjectionFailurePreventsProviderInvocation()
    {
        var providerCalls = 0;
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [new FailingGovernanceSink()],
            new AdvancingTimeProvider());

        var exception = await Assert.ThrowsExactlyAsync<ToolExecutionHookException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreEqual("hook-governance", exception.HookId);
        Assert.AreEqual("project", exception.Phase);
        Assert.AreEqual(0, providerCalls);
    }

    [TestMethod]
    public async Task HookCancellationIsNotWrappedAndReachesTerminalHooks()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ToolExecutionOutcome? terminal = null;
        var hook = new DelegateHook(
            "cancelled-before",
            0,
            (_, token) => ValueTask.FromCanceled<ToolExecutionHookDecision>(token),
            (_, outcome, _) =>
            {
                terminal = outcome;
                return ValueTask.CompletedTask;
            });
        var lifecycle = new RecordingSink();
        var providerCalls = 0;
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) =>
            {
                providerCalls++;
                return ValueTask.FromResult<JsonElement?>(null);
            }),
            [hook],
            [lifecycle],
            new AdvancingTimeProvider());

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => pipeline.ExecuteAsync(Context(), cancellation.Token).AsTask());

        Assert.IsNotNull(terminal);
        Assert.AreEqual(0, providerCalls);
        Assert.AreEqual(ToolExecutionOutcomeKind.Cancelled, terminal.Kind);
        var failed = Assert.IsInstanceOfType<ToolExecutionFailed>(lifecycle.Events[^1]);
        Assert.AreEqual(ToolExecutionFailureKind.Cancelled, failed.FailureKind);
    }

    [TestMethod]
    public async Task TerminalHookFailureDoesNotMaskProviderException()
    {
        var expected = new ProviderDiagnosticException("provider diagnostic");
        var hook = new DelegateHook(
            "broken-terminal",
            0,
            null,
            (_, _, _) => ValueTask.FromException(new InvalidOperationException("terminal diagnostic")));
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromException<JsonElement?>(expected)),
            [hook],
            [],
            new AdvancingTimeProvider());

        var actual = await Assert.ThrowsExactlyAsync<ProviderDiagnosticException>(
            () => pipeline.ExecuteAsync(Context(), default).AsTask());

        Assert.AreSame(expected, actual);
        var terminalError = actual.Data["Agentstration.ToolExecutionTerminalHookError"]?.ToString();
        Assert.IsNotNull(terminalError);
        Assert.Contains("broken-terminal", terminalError, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task HooksRunAgainForEachPhysicalAtLeastOnceAttempt()
    {
        var beforeCalls = 0;
        var hook = new DelegateHook(
            "counter",
            0,
            (_, _) =>
            {
                beforeCalls++;
                return ValueTask.FromResult(ToolExecutionHookDecision.Allowed);
            },
            null);
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(null)),
            [hook],
            [],
            new AdvancingTimeProvider());

        await pipeline.ExecuteAsync(Context() with { InvocationId = "attempt-1" }, default);
        await pipeline.ExecuteAsync(Context() with { InvocationId = "attempt-2" }, default);

        Assert.AreEqual(2, beforeCalls);
    }

    [TestMethod]
    public void DuplicateHookIdsAreRejectedDeterministically()
    {
        var hooks = new IToolExecutionHook[]
        {
            new DelegateHook("duplicate", 0, null, null),
            new DelegateHook("duplicate", 10, null, null)
        };

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(null)),
            hooks,
            [],
            TimeProvider.System));
    }

    [TestMethod]
    public async Task DependencyInjectionComposesLocallyRegisteredHooks()
    {
        var hookCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IToolInvoker>(new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(null)));
        services.AddSingleton<IToolExecutionHook>(new DelegateHook(
            "local-hook",
            0,
            (_, _) =>
            {
                hookCalls++;
                return ValueTask.FromResult(ToolExecutionHookDecision.Allowed);
            },
            null));
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IToolExecutionPipeline>().ExecuteAsync(Context(), default);

        Assert.AreEqual(1, hookCalls);
    }

    [TestMethod]
    public async Task RuntimeProjectionKeepsOneLogicalCallAndTracksPhysicalAttempts()
    {
        var context = Context() with { InvocationId = "attempt-1" };
        var store = new ProjectionRuntimeRunStore(Run());
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { secret = "result" }))),
            [new RuntimeToolExecutionEventSink(new RuntimeRunStateManager(store, TimeProvider.System))],
            new AdvancingTimeProvider());

        await pipeline.ExecuteAsync(context, default);
        await pipeline.ExecuteAsync(context with { InvocationId = "attempt-2" }, default);

        Assert.HasCount(1, store.Current.Value.Status.ToolCalls);
        var projected = store.Current.Value.Status.ToolCalls[0];
        Assert.AreEqual("logical-call", projected.Id);
        Assert.AreEqual("attempt-2", projected.InvocationId);
        Assert.AreEqual(2, projected.Attempt);
        Assert.AreEqual(RuntimeRunState.Succeeded, projected.State);
        Assert.AreEqual(50d, projected.DurationMilliseconds);
        Assert.IsNull(projected.Arguments);
        Assert.IsNull(projected.Result);
        Assert.IsEmpty(projected.Governance);
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeRunEventKind.ToolCallStarted,
                RuntimeRunEventKind.ToolCallGovernanceEvaluated,
                RuntimeRunEventKind.ToolCallCompleted,
                RuntimeRunEventKind.ToolCallStarted,
                RuntimeRunEventKind.ToolCallGovernanceEvaluated,
                RuntimeRunEventKind.ToolCallCompleted
            },
            store.Events.Select(runEvent => runEvent.Kind).ToArray());
        Assert.IsTrue(store.Events.All(runEvent => runEvent.ToolCall is not null));
    }

    [TestMethod]
    public async Task RuntimeProjectionRetainsArgumentsOnlyWhenExplicitlyEnabled()
    {
        var store = new ProjectionRuntimeRunStore(Run());
        var capture = new ToolExecutionCaptureOptions
        {
            PersistArguments = true,
            MaximumArgumentsLength = 1024
        };
        var pipeline = new ToolExecutionPipeline(
            new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(null)),
            [new RuntimeToolExecutionEventSink(new RuntimeRunStateManager(store, TimeProvider.System, capture))],
            new AdvancingTimeProvider());

        await pipeline.ExecuteAsync(Context(), default);

        Assert.AreEqual("{\"secret\":\"argument\"}", store.Current.Value.Status.ToolCalls[0].Arguments);
        Assert.IsTrue(store.Events.All(runEvent => runEvent.ToolCall?.Arguments == "{\"secret\":\"argument\"}"));
    }

    [TestMethod]
    public void ArgumentCaptureIsBoundedWhenEnabled()
    {
        var capture = new ToolExecutionCaptureOptions
        {
            PersistArguments = true,
            MaximumArgumentsLength = 64
        };

        var value = capture.CaptureArguments(JsonSerializer.SerializeToElement(new { secret = new string('x', 100) }));

        Assert.IsNotNull(value);
        Assert.AreEqual(64, value.Length);
        Assert.StartsWith("{\"secret\":\"xxx", value);
        Assert.EndsWith("… [truncated]", value);
    }

    [TestMethod]
    public void RunOverrideWinsTheHostArgumentRetentionDefault()
    {
        var arguments = JsonSerializer.SerializeToElement(new { query = "dotnet" });
        var disabledHost = new ToolExecutionCaptureOptions { PersistArguments = false };
        var enabledHost = new ToolExecutionCaptureOptions { PersistArguments = true };

        Assert.AreEqual("{\"query\":\"dotnet\"}", disabledHost.CaptureArguments(arguments, persistArgumentsOverride: true));
        Assert.IsNull(enabledHost.CaptureArguments(arguments, persistArgumentsOverride: false));
    }

    [TestMethod]
    public async Task FlowProjectionUsesTheSamePipelineAndOmitsArgumentsAndResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-tool-flow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")}");
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IFlowRepository>();
            await repository.InitializeAsync(default);
            await repository.CreateRunAsync(FlowRun(), default);
            var published = new RecordingFlowRunEventSink();
            var pipeline = new ToolExecutionPipeline(
                new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { secret = "result" }))),
                [new FlowToolExecutionEventSink(repository, published)],
                new AdvancingTimeProvider());

            await pipeline.ExecuteAsync(Context() with { OwnerKind = ToolExecutionOwnerKind.FlowRun }, default);

            var events = await repository.ListRunEventsAsync(Workspace, "run-1", 0, default);
            Assert.HasCount(3, events);
            Assert.AreEqual(FlowRunEventType.ToolCallStarted, events[0].Type);
            Assert.AreEqual(FlowRunEventType.ToolCallGovernanceEvaluated, events[1].Type);
            Assert.AreEqual(FlowRunEventType.ToolCallCompleted, events[2].Type);
            Assert.HasCount(3, published.Events);
            var payload = events[1].Payload?.GetRawText();
            Assert.IsNotNull(payload);
            Assert.DoesNotContain("argument", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("result", payload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("logical-call", payload, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlowProjectionRetainsArgumentsWhenExplicitlyEnabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-tool-flow-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")}");
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IFlowRepository>();
            await repository.InitializeAsync(default);
            await repository.CreateRunAsync(FlowRun(), default);
            var pipeline = new ToolExecutionPipeline(
                new DelegateInvoker((_, _) => ValueTask.FromResult<JsonElement?>(null)),
                [new FlowToolExecutionEventSink(
                    repository,
                    new RecordingFlowRunEventSink(),
                    new ToolExecutionCaptureOptions { PersistArguments = true })],
                new AdvancingTimeProvider());

            await pipeline.ExecuteAsync(Context() with { OwnerKind = ToolExecutionOwnerKind.FlowRun }, default);

            var events = await repository.ListRunEventsAsync(Workspace, "run-1", 0, default);
            var arguments = events[1].Payload?.GetProperty("Arguments").GetString();
            Assert.AreEqual("{\"secret\":\"argument\"}", arguments);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ToolExecutionContext Context() => new()
    {
        OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
        ToolCallId = "logical-call",
        InvocationId = "attempt-1",
        ToolId = "tool-resource",
        ToolName = "lookup",
        ToolProviderId = "provider-resource",
        ExternalToolId = "lookup_external",
        WorkspaceId = Workspace,
        RunId = "run-1",
        CorrelationId = "correlation-1",
        Arguments = JsonSerializer.SerializeToElement(new { secret = "argument" })
    };

    private static RuntimeRun Run() => new()
    {
        WorkspaceId = Workspace,
        Scope = new RuntimeRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
        Id = "run-1",
        Name = "run-1",
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference("agent", 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions()
        },
        Status = new RuntimeRunStatus
        {
            State = RuntimeRunState.Running,
            CreatedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero)
        }
    };

    private static FlowRun FlowRun()
    {
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"));
        var version = new FlowVersion(
            Workspace,
            new FlowId("flow"),
            "1.0.0",
            null,
            definition,
            new Dictionary<string, string>(),
            new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero));
        return new FlowRun
        {
            WorkspaceId = Workspace,
            Id = "run-1",
            FlowId = version.FlowId,
            FlowVersion = version.Version,
            Trigger = FlowRunTrigger.Manual,
            Scope = new FlowRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
            Input = JsonSerializer.SerializeToElement(new { prompt = "test" }),
            CreatedAt = version.PublishedAt,
            DefinitionSnapshot = version
        };
    }

    private static WorkspaceId Workspace { get; } = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class RecordingSink : IToolExecutionEventSink
    {
        public List<ToolExecutionLifecycleEvent> Events { get; } = [];

        public ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(executionEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingFlowRunEventSink : IFlowRunEventSink
    {
        public List<FlowRunEvent> Events { get; } = [];

        public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken)
        {
            Events.Add(runEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingTerminalSink : IToolExecutionEventSink
    {
        public ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken = default) =>
            executionEvent is ToolExecutionFailed
                ? ValueTask.FromException(new InvalidOperationException("projection failed"))
                : ValueTask.CompletedTask;
    }

    private sealed class FailingGovernanceSink : IToolExecutionEventSink
    {
        public ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken = default) =>
            executionEvent is ToolExecutionGovernanceEvaluated
                ? ValueTask.FromException(new InvalidOperationException("governance projection failed"))
                : ValueTask.CompletedTask;
    }

    private sealed class DelegateInvoker(
        Func<ToolExecutionContext, CancellationToken, ValueTask<JsonElement?>> invoke) : IToolInvoker
    {
        public ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            invoke(context, cancellationToken);
    }

    private static IToolExecutionHook SequenceHook(string id, int order, List<string> calls) => new DelegateHook(
        id,
        order,
        (_, _) =>
        {
            calls.Add($"before:{id}");
            return ValueTask.FromResult(ToolExecutionHookDecision.Allowed);
        },
        (_, outcome, _) =>
        {
            calls.Add($"after:{id}:{outcome.Kind.ToString().ToLowerInvariant()}");
            return ValueTask.CompletedTask;
        });

    private sealed class DelegateHook(
        string id,
        int order,
        Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecutionHookDecision>>? before,
        Func<ToolExecutionContext, ToolExecutionOutcome, CancellationToken, ValueTask>? after) : IToolExecutionHook
    {
        public string Id { get; } = id;
        public int Order { get; } = order;

        public ValueTask<ToolExecutionHookDecision> BeforeInvokeAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            before?.Invoke(context, cancellationToken) ?? ValueTask.FromResult(ToolExecutionHookDecision.Allowed);

        public ValueTask AfterInvokeAsync(
            ToolExecutionContext context,
            ToolExecutionOutcome outcome,
            CancellationToken cancellationToken = default) =>
            after?.Invoke(context, outcome, cancellationToken) ?? ValueTask.CompletedTask;
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset current = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var value = current;
            current = current.AddMilliseconds(25);
            return value;
        }
    }

    private sealed class ProviderDiagnosticException(string message) : Exception(message);

    private sealed class ProjectionRuntimeRunStore(RuntimeRun run) : IRuntimeRunStore
    {
        private int etag = 1;
        public StoredRuntimeRun Current { get; private set; } = new(run, "1", DateTimeOffset.UtcNow);
        public List<RuntimeRunEvent> Events { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredRuntimeRun> CreateAsync(RuntimeRun value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredRuntimeRun?> GetAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken) =>
            Task.FromResult<StoredRuntimeRun?>(workspaceId == Current.Value.WorkspaceId && runId == Current.Value.Id ? Current : null);
        public Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(WorkspaceId workspaceId, string? agentResourceId, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRunKey>> ListRecoverableAsync(int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(WorkspaceId workspaceId, string runId, string expectedETag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredRuntimeRun> UpdateAsync(RuntimeRun value, string expectedETag, CancellationToken cancellationToken)
        {
            if (expectedETag != Current.ETag) throw new RuntimeRunConcurrencyException("ETag mismatch.");
            Current = new StoredRuntimeRun(value, (++etag).ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow);
            return Task.FromResult(Current);
        }
        public Task<RuntimeRunEvent> AppendEventAsync(RuntimeRunEvent runEvent, CancellationToken cancellationToken)
        {
            var stored = runEvent with { Sequence = Events.Count + 1 };
            Events.Add(stored);
            return Task.FromResult(stored);
        }
        public Task<IReadOnlyList<RuntimeRunEvent>> ListEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RuntimeRunEvent>>(Events.Where(runEvent => runEvent.Sequence > afterSequence).ToArray());
    }
}
