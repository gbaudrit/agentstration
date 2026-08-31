using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed partial class FlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static void AssertValidationCode(string code, FlowDefinition definition)
    {
        var exception = Assert.ThrowsExactly<FlowValidationException>(() => FlowValidator.Validate(Definition($"invalid-{code}", definition)));
        Assert.AreEqual(code, exception.Code);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static FlowResource Definition(string name, FlowDefinition definition) => new(TestScope.WorkspaceId, new FlowId(name), name, null, "1.0.0", true, null, definition, new Dictionary<string, string>(), Now, Now);

    private sealed class FlowFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;
        public FlowService Service => _provider.GetRequiredService<FlowService>();
        public IFlowRepository Repository => _provider.GetRequiredService<IFlowRepository>();
        private FlowFixture(string directory, ServiceProvider provider) { _directory = directory; _provider = provider; }
        public static async Task<FlowFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-flow-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")};Pooling=False");
            services.AddSingleton<FlowService>();
            var provider = services.BuildServiceProvider();
            var fixture = new FlowFixture(directory, provider);
            await fixture.Service.InitializeAsync(default);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class TestFlowRunQueue : IFlowRunQueue
    {
        public List<FlowRunQueueItem> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken) { Enqueued.Add(item); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<FlowRunQueueItem> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
    }

    private sealed class TestFlowRunExecutionScope : IFlowRunExecutionScope
    {
        public ValueTask ValidateAsync(FlowRunScope scope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IDisposable Enter(FlowRunScope scope) => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    private sealed class DeniedFlowRunExecutionScope : IFlowRunExecutionScope
    {
        public ValueTask ValidateAsync(FlowRunScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromException(new FlowValidationException("flow_run_authorization_denied", "Execution permission was revoked."));
        public IDisposable Enter(FlowRunScope scope) => throw new AssertFailedException("A denied scope must not be entered.");
    }

    private static FlowRunScope TestScope { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new(Guid.Parse("22222222-2222-2222-2222-222222222222")), Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private sealed class TestCancellationRegistry : IFlowRunCancellationRegistry
    {
        public CancellationToken Register(FlowRunKey run, CancellationToken stoppingToken) => stoppingToken;
        public bool Cancel(FlowRunKey run) => true;
        public void Complete(FlowRunKey run) { }
    }

    private sealed class TestAgentExecutor : IFlowAgentExecutor
    {
        public Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(new FlowAgentExecutionResult(JsonSerializer.SerializeToElement("done"), $"/agents/{target.Id}", 3, "/profiles/default", "Deterministic", new FlowStepRunUsage(12, 4), ["lookup"], ["executed"]));
    }

    private sealed class TrackingAgentExecutor : IFlowAgentExecutor
    {
        public int ExecutionCount { get; private set; }

        public Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new FlowAgentExecutionResult(JsonSerializer.SerializeToElement("done"), "/agents/agent", 1, "/profiles/default", "Test", null, [], []));
        }
    }

    private sealed class FailingAgentExecutor : IFlowAgentExecutor
    {
        public Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken) =>
            Task.FromException<FlowAgentExecutionResult>(new InvalidOperationException("simulated agent failure"));
    }

    private sealed class TestOrchestrationEngine : IFlowOrchestrationEngine
    {
        public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
            FlowOrchestrationExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(FlowOrchestrationStrategy.Handoff, request.Definition.Strategy);
            Assert.IsTrue(request.Definition.Participants.All(participant =>
                participant.Namespace == new ResourceNamespace("daily-life-assistant")));
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FlowParticipantTurnStarted("researcher", 1);
            yield return new FlowParticipantDelta("researcher", "draft");
            yield return new FlowParticipantTurnCompleted("researcher", 1);
            var researcher = Participant("researcher", 1, "draft");
            yield return new FlowParticipantCompleted(researcher);
            yield return new FlowParticipantHandoff("researcher", "reviewer");
            yield return new FlowParticipantTurnStarted("reviewer", 2);
            yield return new FlowParticipantDelta("reviewer", "reviewed");
            yield return new FlowParticipantTurnCompleted("reviewer", 2);
            var reviewer = Participant("reviewer", 2, "reviewed");
            yield return new FlowParticipantCompleted(reviewer);
            yield return new FlowExecutionCompleted(new FlowOrchestrationResult(
                FlowOrchestrationStrategy.Handoff,
                JsonSerializer.SerializeToElement("reviewed"),
                [researcher, reviewer]));
            await Task.CompletedTask;
        }

        private static FlowParticipantResult Participant(string id, int turn, string output) => new(
            id,
            [new FlowParticipantTurnResult(turn, output)],
            JsonSerializer.SerializeToElement(output),
            id,
            1,
            "default",
            "Deterministic",
            [],
            null);
    }

    private sealed class StalledOrchestrationEngine : IFlowOrchestrationEngine
    {
        public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
            FlowOrchestrationExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ConcurrentTrackingAgentExecutor : IFlowAgentExecutor
    {
        private int executionCount;
        public int ExecutionCount => executionCount;

        public async Task<FlowAgentExecutionResult> ExecuteAsync(
            FlowTargetReference target,
            JsonElement input,
            string correlationId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            await Task.Delay(100, cancellationToken);
            return new(JsonSerializer.SerializeToElement("done"), target.Id, 1, "default", "Test", null, [], []);
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class TestRuntimeExecutionStateStore : IRuntimeExecutionStateStore
    {
        private readonly Dictionary<(WorkspaceId WorkspaceId, string RunId, string RuntimeType, string StateId), RuntimeExecutionState> states = [];

        public Task StoreAsync(RuntimeExecutionState state, CancellationToken cancellationToken)
        {
            states[(state.WorkspaceId, state.RunId, state.RuntimeType, state.StateId)] = state;
            return Task.CompletedTask;
        }

        public Task<RuntimeExecutionState?> GetAsync(
            WorkspaceId workspaceId,
            string runId,
            string runtimeType,
            string stateId,
            CancellationToken cancellationToken)
        {
            states.TryGetValue((workspaceId, runId, runtimeType, stateId), out var state);
            return Task.FromResult(state);
        }

        public Task<IReadOnlyList<RuntimeExecutionState>> ListAsync(
            WorkspaceId workspaceId,
            string runId,
            string runtimeType,
            string? parentStateId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RuntimeExecutionState>>(
                states.Values.Where(state => state.WorkspaceId == workspaceId
                    && state.RunId == runId
                    && state.RuntimeType == runtimeType
                    && (parentStateId is null || state.ParentStateId == parentStateId)).ToArray());

        public Task DeleteAsync(WorkspaceId workspaceId, string runId, string? runtimeType, CancellationToken cancellationToken)
        {
            foreach (var key in states.Keys.Where(key => key.WorkspaceId == workspaceId
                         && key.RunId == runId
                         && (runtimeType is null || key.RuntimeType == runtimeType)).ToArray())
                states.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class SuspendingOrchestrationEngine : IFlowOrchestrationEngine
    {
        public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
            FlowOrchestrationExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var bindings = request.RuntimeBindings is { Count: > 0 }
                ? request.RuntimeBindings
                :
                [
                    Binding("agent-a", 7),
                    Binding("agent-b", 4)
                ];
            yield return new FlowRuntimeBindingsResolved(bindings);
            if (request.AnsweredInput?.Response is null)
            {
                yield return new FlowExternalInputRequested(
                    "runtime-request-1", "What is your name?", InputRequestType.Text, [], "agent-a",
                    new DurableRuntimeStateReference("test-runtime", "state-1", DateTimeOffset.UtcNow));
                yield break;
            }
            Assert.AreEqual(7, bindings.Single(binding => binding.ParticipantId == "agent-a").AgentGeneration);
            var answer = request.AnsweredInput.Response.Value.GetString()!;
            var participant = new FlowParticipantResult(
                "agent-a", [new(1, answer)], JsonSerializer.SerializeToElement(answer), "agent-a", 7,
                "default", "Deterministic", [], null);
            yield return new FlowParticipantCompleted(participant);
            yield return new FlowExecutionCompleted(new FlowOrchestrationResult(
                FlowOrchestrationStrategy.Handoff, JsonSerializer.SerializeToElement(answer), [participant]));
            await Task.CompletedTask;
        }

        private static RuntimeExecutionBinding Binding(string participant, long generation) => new()
        {
            ParticipantId = participant,
            AgentNamespace = ResourceNamespace.Default,
            AgentResourceId = participant,
            AgentGeneration = generation,
            DeploymentId = $"deployment-{participant}-{generation}",
            RevisionId = $"revision-{participant}-{generation}",
            RuntimeProfileName = "local",
            ModelProfileName = "default"
        };
    }

    private sealed record InteractionApiCase(
        string Kind,
        InputRequestType Type,
        IReadOnlyList<string> Options,
        JsonElement ValidValue,
        JsonElement InvalidValue);

    private sealed class TypedSuspendingOrchestrationEngine : IFlowOrchestrationEngine
    {
        public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
            FlowOrchestrationExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = request.Input.GetProperty("kind").GetString();
            var type = kind switch
            {
                "choice" => InputRequestType.Choice,
                "confirmation" => InputRequestType.Confirmation,
                _ => InputRequestType.Text
            };
            var options = type == InputRequestType.Choice ? new[] { "red", "blue" } : [];
            yield return new FlowExternalInputRequested(
                $"runtime-{request.RunId}",
                $"Provide a {kind} response",
                type,
                options,
                "sql-expert",
                new DurableRuntimeStateReference("test-runtime", $"state-{request.RunId}", DateTimeOffset.UtcNow));
            await Task.CompletedTask;
        }
    }

    private sealed class ExistingResourceResolver : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
