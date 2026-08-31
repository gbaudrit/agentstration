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
    public void DirectFlowRequiresExactlyOneTypedTarget()
    {
        var valid = Definition("direct", new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        FlowValidator.Validate(valid);
        var exception = Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("invalid", new DirectFlowDefinition(null!))));
        Assert.AreEqual("flow_target_required", exception.Code);
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("direct-flow",
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Flow, "child")))));
    }

    [TestMethod]
    public async Task OrchestrationFlowUsesNeutralEngineAndPersistsParticipantProgress()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "orchestration-run",
            "Coordinates agents",
            "1.0.0",
            true,
            new OrchestrationFlowDefinition(
                [
                    new FlowTargetReference(FlowTargetKind.Agent, "researcher"),
                    new FlowTargetReference(FlowTargetKind.Agent, "reviewer")
                ],
                new SequentialOrchestrationPattern())), new ResourceNamespace("daily-life-assistant"), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var queue = new TestFlowRunQueue();
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(
            fixture.Repository,
            queue,
            new TestCancellationRegistry(),
            new TestAgentExecutor(),
            new TestOrchestrationEngine(),
            expressions,
            expressions,
            new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(),
            TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Investigate"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "orchestration-correlation", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        CollectionAssert.AreEqual(
            new[] { "Input", "researcher", "reviewer", "Output" },
            completed.Steps.Select(step => step.StepName).ToArray());
        Assert.IsTrue(completed.Steps.All(step => step.Status == FlowStepRunStatus.Succeeded));
        Assert.AreEqual("reviewed", completed.Output!.Value.GetProperty("finalOutput").GetString());
        Assert.HasCount(2, completed.Output.Value.GetProperty("participants").EnumerateArray().ToArray());
        var events = await runs.ListEventsAsync(TestScope, completed.Id, 0, default);
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.StepOutputDelta));
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.ParticipantTurnStarted));
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.ParticipantTurnCompleted));
    }

    [TestMethod]
    public async Task OrchestrationFlowTimeoutPersistsAnExplicitTerminalState()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "orchestration-timeout",
            "Times out a stalled orchestration",
            "1.0.0",
            true,
            new OrchestrationFlowDefinition(
                [
                    new FlowTargetReference(FlowTargetKind.Agent, "agent-a"),
                    new FlowTargetReference(FlowTargetKind.Agent, "agent-b")
                ],
                new SequentialOrchestrationPattern())), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(
            fixture.Repository,
            new TestFlowRunQueue(),
            new TestCancellationRegistry(),
            new TestAgentExecutor(),
            new StalledOrchestrationEngine(),
            expressions,
            expressions,
            new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(),
            TimeProvider.System,
            new FlowRunExecutionOptions { OrchestrationTimeout = TimeSpan.FromMilliseconds(50) });
        using var input = JsonDocument.Parse("""{"prompt":"Wait"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "timeout-correlation", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var timedOut = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut.Status);
        Assert.AreEqual("flow_run_timed_out", timedOut.Error!.Code);
        Assert.IsTrue((await runs.ListEventsAsync(TestScope, timedOut.Id, 0, default)).Any(item => item.Type == FlowRunEventType.FlowRunTimedOut));
    }

    [TestMethod]
    public async Task GraphWithoutFailureTransitionPreservesAgentError()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var graph = new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps = [new AgentFlowStepDefinition { Name = "agent", Agent = new("sql-expert") }],
            Transitions = []
        };
        var now = TimeProvider.System.GetUtcNow();
        var draft = new FlowDraft { WorkspaceId = TestScope.WorkspaceId, Id = "failing-draft", FlowId = new("failing-run"), DisplayName = "Failing run", Definition = graph, CreatedAt = now, UpdatedAt = now };
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(), new FailingAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(), new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("{}");

        var pending = await runs.CreateDraftAsync(draft, FlowRunTrigger.Manual, "tester", "failing-correlation", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, completed.Status);
        Assert.AreEqual("agent_step_failed", completed.Error?.Code);
        Assert.AreEqual("simulated agent failure", completed.Error?.Message);
    }

}

