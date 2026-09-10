using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task FlowCallSuspendsCreatesOneDurableChildAndResumesWithItsOutput()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var queue = new TestFlowRunQueue();
        var runs = Service(fixture, queue);
        using var input = JsonDocument.Parse("""{"article":"new item"}""");

        var pending = await runs.CreateAsync(
            parent.Value.Id, "1.0.0", "local", FlowRunTrigger.WorkItem, "tester", "root-correlation", input.RootElement,
            null, "interaction-1", "task-1", "message-1", TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var waiting = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.WaitingForChild, waiting.Status);
        Assert.IsNull(waiting.ExecutionLeaseId);
        var callStep = waiting.Steps.Single(step => step.StepName == "analyze");
        Assert.IsNotNull(callStep.ChildFlowRunId);
        var child = (await runs.GetAsync(TestScope.WorkspaceId, callStep.ChildFlowRunId, default))!.Value;
        Assert.AreEqual(waiting.Id, child.ParentFlowRunId);
        Assert.AreEqual(waiting.Id, child.RootFlowRunId);
        Assert.AreEqual(1, child.NestingDepth);
        Assert.AreEqual("1.0.0", child.FlowVersion);
        Assert.AreEqual(FlowRunTrigger.Flow, child.Trigger);
        Assert.AreEqual(waiting.Scope, child.Scope);
        Assert.AreEqual(waiting.WorkTaskId, child.WorkTaskId);
        Assert.AreEqual(waiting.InteractionId, child.InteractionId);
        var otherWorkspace = TestScope with { WorkspaceId = new WorkspaceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")) };
        Assert.IsNull(await runs.GetAsync(child.Id, otherWorkspace, default));

        await runs.ExecuteAsync(new(waiting.Id, TestScope), default);
        var redelivered = (await runs.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.WaitingForChild, redelivered.Status);
        Assert.AreEqual(callStep.ChildFlowRunId, redelivered.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId);
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);

        await runs.ExecuteAsync(new(child.Id, TestScope), default);
        var resumed = (await runs.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Pending, resumed.Status);
        await runs.ExecuteAsync(new(resumed.Id, TestScope), default);

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual("new item", completed.Output?.GetProperty("summary").GetString());
        Assert.AreEqual("new item", completed.Steps.Single(step => step.StepName == "analyze").Output?.GetProperty("summary").GetString());
        var events = await runs.ListEventsAsync(TestScope, completed.Id, 0, default);
        Assert.AreEqual(1, events.Count(item => item.Type == FlowRunEventType.ChildFlowRunCreated));
        Assert.AreEqual(1, events.Count(item => item.Type == FlowRunEventType.FlowRunResumedFromChild));
    }

    [TestMethod]
    public async Task CancellingAWaitingParentPropagatesToItsActiveChild()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var pending = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "cancel-tree", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var waiting = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        var childId = waiting.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;

        await runs.CancelAsync(waiting.Id, TestScope, default);

        Assert.AreEqual(FlowRunStatus.Cancelled, (await runs.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value.Status);
        Assert.AreEqual(FlowRunStatus.Cancelled, (await runs.GetAsync(TestScope.WorkspaceId, childId, default))!.Value.Status);
    }

    [TestMethod]
    public async Task RecoveryRequeuesAWaitingParentWhenItsDeterministicChildIsMissing()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var firstQueue = new TestFlowRunQueue();
        var first = Service(fixture, firstQueue);
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var pending = await first.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "recover-child", input.RootElement, TestScope, default);
        await first.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var waiting = (await first.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!;
        var childId = waiting.Value.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;
        var child = (await first.GetAsync(TestScope.WorkspaceId, childId, default))!;
        await fixture.Repository.DeleteRunAsync(TestScope.WorkspaceId, childId, child.ETag, default);

        var recoveryQueue = new TestFlowRunQueue();
        var recovered = Service(fixture, recoveryQueue);
        await recovered.InitializeAsync(default);
        Assert.IsTrue(recoveryQueue.Enqueued.Any(item => item.RunId == waiting.Value.Id));

        await recovered.ExecuteAsync(new(waiting.Value.Id, TestScope), default);
        var recreated = await recovered.GetAsync(TestScope.WorkspaceId, childId, default);
        Assert.IsNotNull(recreated);
        Assert.AreEqual(waiting.Value.Id, recreated.Value.ParentFlowRunId);
        Assert.HasCount(2, (await recovered.ListAsync(null, null, 0, 20, TestScope, default)).Items);
    }

    [TestMethod]
    public async Task ChildFailureResumesTheExplicitFailedTransition()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", FailingChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var pending = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "child-failure", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var waiting = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        var childId = waiting.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;

        await runs.ExecuteAsync(new(childId, TestScope), default);
        await runs.ExecuteAsync(new(waiting.Id, TestScope), default);

        var failed = (await runs.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failed.Status);
        Assert.AreEqual("CHILD_FLOW_FAILED", failed.Error?.Code);
        var call = failed.Steps.Single(step => step.StepName == "analyze");
        Assert.AreEqual(FlowStepRunStatus.Failed, call.Status);
        Assert.AreEqual("analyze-failed", call.SelectedTransition);
        Assert.AreEqual("CHILD_FAILURE", call.Error?.Code);
    }

    [TestMethod]
    public async Task NestedFlowDepthIsBoundedWithAStableFailure()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "grandchild", ChildGraph());
        await CreatePublishedGraphAsync(fixture, "child", CallingGraph("grandchild"));
        var parent = await CreatePublishedGraphAsync(fixture, "parent", CallingGraph("child"));
        var runs = Service(fixture, new TestFlowRunQueue(), new FlowRunExecutionOptions { MaximumNestingDepth = 1 });
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var root = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "bounded", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);
        var parentWaiting = (await runs.GetAsync(TestScope.WorkspaceId, root.Value.Id, default))!.Value;
        var childId = parentWaiting.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;

        await runs.ExecuteAsync(new(childId, TestScope), default);

        var failedChild = (await runs.GetAsync(TestScope.WorkspaceId, childId, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failedChild.Status);
        Assert.AreEqual("flow_nesting_depth_exceeded", failedChild.Error?.Code);
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
    }

    [TestMethod]
    public async Task DescendantRunCountIsBoundedWithAStableFailure()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", TwoCallsGraph());
        var runs = Service(fixture, new TestFlowRunQueue(), new FlowRunExecutionOptions { MaximumDescendantRuns = 1 });
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var root = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "bounded-count", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);
        var waiting = (await runs.GetAsync(TestScope.WorkspaceId, root.Value.Id, default))!.Value;
        var firstChildId = waiting.Steps.Single(step => step.StepName == "first").ChildFlowRunId!;
        await runs.ExecuteAsync(new(firstChildId, TestScope), default);
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);

        var failed = (await runs.GetAsync(TestScope.WorkspaceId, root.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failed.Status);
        Assert.AreEqual("flow_descendant_limit_exceeded", failed.Error?.Code);
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
    }

    [TestMethod]
    public async Task ChildTimeoutResumesTheExplicitTimeoutTransition()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var child = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "child",
            null,
            "1.0.0",
            true,
            new OrchestrationFlowDefinition(
                [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
                new SequentialOrchestrationPattern())), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, child.Value.Id, "1.0.0", true, default);
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var runs = Service(
            fixture,
            new TestFlowRunQueue(),
            new FlowRunExecutionOptions { OrchestrationTimeout = TimeSpan.FromMilliseconds(25) },
            new StalledOrchestrationEngine());
        using var input = JsonDocument.Parse("""{"article":"new item"}""");
        var root = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "timeout-child", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);
        var waiting = (await runs.GetAsync(TestScope.WorkspaceId, root.Value.Id, default))!.Value;
        var childId = waiting.Steps.Single(step => step.StepName == "analyze").ChildFlowRunId!;

        await runs.ExecuteAsync(new(childId, TestScope), default);
        await runs.ExecuteAsync(new(root.Value.Id, TestScope), default);

        var failed = (await runs.GetAsync(TestScope.WorkspaceId, root.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failed.Status);
        var call = failed.Steps.Single(step => step.StepName == "analyze");
        Assert.AreEqual("analyze-timeout", call.SelectedTransition);
        Assert.AreEqual("flow_run_timed_out", call.Error?.Code);
    }

    private static FlowRunService Service(
        FlowFixture fixture,
        TestFlowRunQueue queue,
        FlowRunExecutionOptions? options = null,
        IFlowOrchestrationEngine? orchestration = null)
    {
        var expressions = new FlowExpressionParser();
        return new FlowRunService(
            fixture.Repository,
            queue,
            new TestCancellationRegistry(),
            new TestAgentExecutor(),
            orchestration ?? new UnsupportedFlowOrchestrationEngine(),
            expressions,
            expressions,
            new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(),
            TimeProvider.System,
            options);
    }

    private static async Task<Agentstration.Flow.Storage.Abstractions.StoredFlow> CreatePublishedGraphAsync(
        FlowFixture fixture,
        string name,
        FlowGraphDefinition graph)
    {
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            name,
            null,
            "1.0.0",
            true,
            PlaceholderDefinition(),
            Graph: graph), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        return created;
    }

    private static FlowGraphDefinition ChildGraph() => new()
    {
        EntryStep = "input",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } }, required = new[] { "article" } }),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new TransformFlowStepDefinition { Name = "summarize", Mapping = JsonSerializer.SerializeToElement(new { summary = "${input.article}" }) },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.summarize.output}") }
        ],
        Transitions =
        [
            new("input-summarize", "input", "completed", "summarize"),
            new("summarize-output", "summarize", "completed", "output")
        ]
    };

    private static FlowGraphDefinition FailingChildGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new FailureFlowStepDefinition { Name = "failure", Code = "CHILD_FAILURE", Message = "The child failed." }
        ],
        Transitions = [new("input-failure", "input", "completed", "failure")]
    };

    private static FlowGraphDefinition CallingGraph(string childName)
    {
        var graph = ParentGraph();
        return graph with
        {
            Steps = graph.Steps.Select(step => step is FlowCallStepDefinition call
                ? call with { Flow = new(childName, FlowCallVersionStrategy.Exact, "1.0.0") }
                : step).ToArray()
        };
    }

    private static FlowGraphDefinition TwoCallsGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new FlowCallStepDefinition { Name = "first", Flow = new("child", FlowCallVersionStrategy.Exact, "1.0.0"), InputMapping = JsonSerializer.SerializeToElement(new { article = "${input.article}" }) },
            new FlowCallStepDefinition { Name = "second", Flow = new("child", FlowCallVersionStrategy.Exact, "1.0.0"), InputMapping = JsonSerializer.SerializeToElement(new { article = "${input.article}" }) },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.second.output}") }
        ],
        Transitions =
        [
            new("input-first", "input", "completed", "first"),
            new("first-second", "first", "completed", "second"),
            new("second-output", "second", "completed", "output")
        ]
    };

    private static FlowGraphDefinition ParentGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new FlowCallStepDefinition
            {
                Name = "analyze",
                Flow = new("child", FlowCallVersionStrategy.Exact, "1.0.0"),
                InputMapping = JsonSerializer.SerializeToElement(new { article = "${input.article}" })
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.analyze.output}") },
            new FailureFlowStepDefinition { Name = "failure", Code = "CHILD_FLOW_FAILED", Message = "Child Flow failed." }
        ],
        Transitions =
        [
            new("input-analyze", "input", "completed", "analyze"),
            new("analyze-output", "analyze", "completed", "output"),
            new("analyze-failed", "analyze", "failed", "failure"),
            new("analyze-timeout", "analyze", "timedOut", "failure"),
            new("analyze-cancelled", "analyze", "cancelled", "failure")
        ]
    };
}
