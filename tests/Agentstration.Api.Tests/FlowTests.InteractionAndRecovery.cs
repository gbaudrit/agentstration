using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
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
    public async Task InteractiveResponseSurvivesReconstructionAndRequeuesTheAnsweredRun()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "interactive-run", null, "1.0.0", true,
            new OrchestrationFlowDefinition(
                [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
                new HandoffOrchestrationPattern("agent-a", [new("agent-a", "agent-b")]))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var firstService = new FlowRunService(
            fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(), new TestAgentExecutor(),
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Need a name"}""");

        var pending = await firstService.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "interactive-correlation", input.RootElement, TestScope, default);
        var waiting = await PutWaitingForInputAsync(fixture.Repository, pending, TimeProvider.System);
        Assert.AreEqual(FlowRunStatus.WaitingForInput, waiting.Status);
        Assert.AreEqual(7, waiting.RuntimeBindings.Single(binding => binding.ParticipantId == "agent-a").AgentGeneration);
        Assert.IsNotNull(waiting.RuntimeState);
        var request = (await firstService.ListInputsAsync(waiting.Id, InputRequestStatus.Pending, TestScope, default)).Single();

        var lostQueue = new TestFlowRunQueue();
        var reconstructed = new FlowRunService(
            fixture.Repository, lostQueue, new TestCancellationRegistry(), new TestAgentExecutor(),
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        await reconstructed.RespondAsync(waiting.Id, request.Value.Id, JsonSerializer.SerializeToElement("Ada"), "principal-1", TestScope, default);
        await Assert.ThrowsExactlyAsync<InputRequestAlreadyResolvedException>(() => reconstructed.RespondAsync(
            waiting.Id, request.Value.Id, JsonSerializer.SerializeToElement("Grace"), "principal-2", TestScope, default));

        var recoveryQueue = new TestFlowRunQueue();
        var recovered = new FlowRunService(
            fixture.Repository, recoveryQueue, new TestCancellationRegistry(), new TestAgentExecutor(),
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), TimeProvider.System);
        await recovered.InitializeAsync(default);
        Assert.IsTrue(recoveryQueue.Enqueued.Any(item => item.RunId == waiting.Id));
        var answered = (await recovered.ListInputsAsync(waiting.Id, InputRequestStatus.Answered,
            TestScope, default)).Single().Value;
        Assert.AreEqual("Ada", answered.Response?.Value.GetString());
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
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, events,
            new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Need input"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "retention-correlation", input.RootElement, TestScope, default);
        await PutWaitingForInputAsync(fixture.Repository, pending, TimeProvider.System);

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
            new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(), clock,
            new FlowRunExecutionOptions { InputRequestTimeout = TimeSpan.FromMinutes(1) });
        using var input = JsonDocument.Parse("""{"prompt":"Wait"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual,
            "tester", "input-timeout", input.RootElement, TestScope, default);
        await PutWaitingForInputAsync(fixture.Repository, pending, clock);
        clock.Advance(TimeSpan.FromMinutes(2));

        await runs.ExpireDueInputsAsync(default);

        var timedOut = (await runs.GetAsync(pending.Value.Id, TestScope, default))!.Value;
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut.Status);
        Assert.AreEqual("input_request_timed_out", timedOut.Error?.Code);
        Assert.AreEqual(InputRequestStatus.Expired, (await runs.ListInputsAsync(pending.Value.Id, null, TestScope, default)).Single().Value.Status);
    }

    private static async Task<FlowRun> PutWaitingForInputAsync(
        IFlowRepository repository,
        StoredFlowRun pending,
        TimeProvider timeProvider,
        InputRequestType type = InputRequestType.Text,
        IReadOnlyList<string>? options = null)
    {
        var now = timeProvider.GetUtcNow();
        var waiting = pending.Value with
        {
            Status = FlowRunStatus.WaitingForInput,
            StartedAt = pending.Value.StartedAt ?? now,
            RuntimeState = new("test-runtime", "state-1", now),
            RuntimeBindings =
            [
                new RuntimeExecutionBinding
                {
                    ParticipantId = "agent-a",
                    AgentNamespace = ResourceNamespace.Default,
                    AgentResourceId = "agent-a",
                    AgentGeneration = 7,
                    DeploymentId = "deployment-agent-a-7",
                    RevisionId = "revision-agent-a-7",
                    RuntimeProfileName = "local",
                    ModelProfileName = "default"
                }
            ]
        };
        await repository.UpdateRunAsync(waiting, pending.ETag, default);
        await repository.CreateInputRequestAsync(new InputRequest
        {
            WorkspaceId = waiting.WorkspaceId,
            Id = $"input-{Guid.NewGuid():N}",
            RunId = waiting.Id,
            RuntimeRequestId = "runtime-request-1",
            Prompt = "What is your name?",
            Type = type,
            Options = options ?? [],
            Source = "agent-a",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(1)
        }, default);
        return waiting;
    }

}

