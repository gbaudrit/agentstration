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

}

