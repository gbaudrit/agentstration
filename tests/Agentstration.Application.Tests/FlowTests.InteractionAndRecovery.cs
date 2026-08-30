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

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task InteractiveOrchestrationSurvivesReconstructionAndRecoversAnAnsweredRun()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "interactive-run", null, "1.0.0", true,
            new OrchestrationFlowDefinition(
                [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
                new HandoffOrchestrationPattern("agent-a", [new("agent-a", "agent-b")]))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var firstQueue = new TestFlowRunQueue();
        var firstService = new FlowRunService(
            fixture.Repository, firstQueue, new TestCancellationRegistry(), new TestAgentExecutor(),
            new SuspendingOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Need a name"}""");

        var pending = await firstService.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "interactive-correlation", input.RootElement, TestScope, default);
        await firstService.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var waiting = (await firstService.GetAsync(pending.Value.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.WaitingForInput, waiting.Status);
        Assert.AreEqual(7, waiting.RuntimeBindings.Single(binding => binding.ParticipantId == "agent-a").AgentGeneration);
        Assert.IsNotNull(waiting.RuntimeState);
        var request = (await firstService.ListInputsAsync(waiting.Id, InputRequestStatus.Pending, TestScope, default)).Single();

        var lostQueue = new TestFlowRunQueue();
        var reconstructed = new FlowRunService(
            fixture.Repository, lostQueue, new TestCancellationRegistry(), new TestAgentExecutor(),
            new SuspendingOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        await reconstructed.RespondAsync(waiting.Id, request.Value.Id, JsonSerializer.SerializeToElement("Ada"), "principal-1", TestScope, default);
        await Assert.ThrowsExactlyAsync<InputRequestAlreadyResolvedException>(() => reconstructed.RespondAsync(
            waiting.Id, request.Value.Id, JsonSerializer.SerializeToElement("Grace"), "principal-2", TestScope, default));

        var recoveryQueue = new TestFlowRunQueue();
        var recovered = new FlowRunService(
            fixture.Repository, recoveryQueue, new TestCancellationRegistry(), new TestAgentExecutor(),
            new SuspendingOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        await recovered.InitializeAsync(default);
        Assert.IsTrue(recoveryQueue.Enqueued.Any(item => item.RunId == waiting.Id));
        await recovered.ExecuteAsync(new(waiting.Id, TestScope), default);

        var completed = (await recovered.GetAsync(waiting.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual("Ada", completed.Output!.Value.GetProperty("finalOutput").GetString());
        Assert.AreEqual(7, completed.RuntimeBindings.Single(binding => binding.ParticipantId == "agent-a").AgentGeneration);
    }

    [TestMethod]
    public async Task RevisionUsageDistinguishesActiveAndHistoricalRunsAndForceTerminationIsExplicit()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "revision-retention", null, "1.0.0", true,
            new OrchestrationFlowDefinition(
                [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
                new HandoffOrchestrationPattern("agent-a", [new("agent-a", "agent-b")]))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var cancellations = new TestCancellationRegistry();
        var events = new NullFlowRunEventSink();
        var runs = new FlowRunService(
            fixture.Repository, new TestFlowRunQueue(), cancellations, new TestAgentExecutor(),
            new SuspendingOrchestrationEngine(), expressions, expressions, events,
            new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Need input"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "retention-correlation", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var active = await runs.GetRevisionUsageAsync("revision-agent-a-7", default);
        Assert.AreEqual(1, active.ActiveRunCount);
        Assert.AreEqual(1, active.WaitingForInputCount);
        Assert.AreEqual(0, active.HistoricalRunCount);
        Assert.AreEqual(FlowRunStatus.WaitingForInput, active.ActiveRuns.Single().Status);
        Assert.AreEqual(1, active.ActiveRuns.Single().PendingInputRequestCount);

        var executionStates = new TestRuntimeExecutionStateStore();
        await executionStates.StoreAsync(new RuntimeExecutionState(
            TestScope.WorkspaceId, pending.Value.Id, "maf", "checkpoint-1", JsonSerializer.SerializeToElement(new { state = "waiting" }), Now), default);
        var retention = new AgentRevisionRunRetention(
            new FlowRevisionRetentionService(fixture.Repository, cancellations, events, TimeProvider.System),
            executionStates);
        await retention.ForceTerminateAsync("revision-agent-a-7", default);

        var cancelled = (await runs.GetAsync(pending.Value.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Cancelled, cancelled.Status);
        Assert.AreEqual("runtime_dependency_force_purged", cancelled.Error?.Code);
        Assert.AreEqual(InputRequestStatus.Cancelled,
            (await runs.ListInputsAsync(pending.Value.Id, null, TestScope, default)).Single().Value.Status);
        Assert.IsNull(await executionStates.GetAsync(TestScope.WorkspaceId, pending.Value.Id, "maf", "checkpoint-1", default));
        Assert.IsTrue((await runs.ListEventsAsync(TestScope, pending.Value.Id, 0, default))
            .Any(runEvent => runEvent.Type == FlowRunEventType.FlowRunCancelled));
        var historical = await runs.GetRevisionUsageAsync("revision-agent-a-7", default);
        Assert.AreEqual(0, historical.ActiveRunCount);
        Assert.AreEqual(1, historical.HistoricalRunCount);
    }

    [TestMethod]
    public async Task ConcurrentWorkersClaimARunOnlyOnce()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "single-claim", null, "1.0.0", true, new DirectFlowDefinition(new(FlowTargetKind.Agent, "agent-a"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var executor = new ConcurrentTrackingAgentExecutor();
        FlowRunService Worker() => new(
            fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(), executor,
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        var first = Worker();
        var second = Worker();
        using var input = JsonDocument.Parse("""{"prompt":"once"}""");
        var pending = await first.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "single-claim", input.RootElement, TestScope, default);

        await Task.WhenAll(first.ExecuteAsync(new(pending.Value.Id, TestScope), default), second.ExecuteAsync(new(pending.Value.Id, TestScope), default));

        Assert.AreEqual(1, executor.ExecutionCount);
        var completed = (await first.GetAsync(pending.Value.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.IsNull(completed.ExecutionLeaseId);
    }

    [TestMethod]
    public async Task PendingInputExpiresDeterministicallyAndTimesOutTheRun()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "input-timeout", null, "1.0.0", true,
            new OrchestrationFlowDefinition(
                [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
                new HandoffOrchestrationPattern("agent-a", [new("agent-a", "agent-b")]))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var clock = new AdvancingTimeProvider(new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero));
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(
            fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(), new TestAgentExecutor(),
            new SuspendingOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), clock,
            new FlowRunExecutionOptions { InputRequestTimeout = TimeSpan.FromMinutes(1) });
        using var input = JsonDocument.Parse("""{"prompt":"Wait"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "input-timeout", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        clock.Advance(TimeSpan.FromMinutes(2));

        await runs.ExpireDueInputsAsync(default);

        var timedOut = (await runs.GetAsync(pending.Value.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut.Status);
        Assert.AreEqual("input_request_timed_out", timedOut.Error?.Code);
        Assert.AreEqual(InputRequestStatus.Expired, (await runs.ListInputsAsync(pending.Value.Id, null, TestScope, default)).Single().Value.Status);
    }

}

