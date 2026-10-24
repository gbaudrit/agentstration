using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    [DataRow("empty")]
    [DataRow("sequential")]
    public void NewDraftTemplatesUseTheIncomingTransitionOutput(string template)
    {
        var graph = FlowDraftTemplates.Create(template);
        var output = graph.Steps.OfType<OutputFlowStepDefinition>().Single(step => step.Outcome is not FlowOutputOutcome.Error);
        var error = graph.Steps.OfType<OutputFlowStepDefinition>().Single(step => step.Outcome == FlowOutputOutcome.Error);

        Assert.AreEqual("${transition.output}", output.OutputMapping?.GetString());
        Assert.AreEqual("error", error.Name);
    }

    [TestMethod]
    public async Task DirectInputOutputKeepsTheCanonicalMappingAfterStorageReload()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var graph = FlowDraftTemplates.Create("empty");
        var created = await CreatePublishedGraphAsync(fixture, "direct-output", graph);
        var reloaded = await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", default);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("${transition.output}", reloaded.Value.Graph!.Steps.OfType<OutputFlowStepDefinition>().Single(step => step.Outcome is not FlowOutputOutcome.Error).OutputMapping?.GetString());
        StringAssert.Contains(FlowDraftService.ToYaml(reloaded.Value.Graph), "${transition.output}");
    }

    [TestMethod]
    public async Task AssignedFlowCallCreatesOneDurableChildWithInheritedContextAndIdempotentIdentity()
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
        var childInput = JsonSerializer.SerializeToElement(new { article = "new item" });
        var first = await runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, pending.Value.Id,
            "analyze", childInput, "flowCall", null, default);
        var replay = await runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, pending.Value.Id,
            "analyze", childInput, "flowCall", null, default);

        var child = first.Value;
        Assert.AreEqual(first.Value.Id, replay.Value.Id);
        Assert.AreEqual(pending.Value.Id, child.ParentFlowRunId);
        Assert.AreEqual(pending.Value.Id, child.RootFlowRunId);
        Assert.AreEqual(1, child.NestingDepth);
        Assert.AreEqual("1.0.0", child.FlowVersion);
        Assert.AreEqual(FlowRunTrigger.Flow, child.Trigger);
        Assert.AreEqual(pending.Value.Scope, child.Scope);
        Assert.AreEqual(pending.Value.WorkTaskId, child.WorkTaskId);
        Assert.AreEqual(pending.Value.InteractionId, child.InteractionId);
        var otherWorkspace = TestScope with { WorkspaceId = new WorkspaceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")) };
        Assert.IsNull(await runs.GetAsync(child.Id, otherWorkspace, default));
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
        Assert.IsTrue(queue.Enqueued.Any(item => item.RunId == child.Id));
        var events = await runs.ListEventsAsync(TestScope, pending.Value.Id, 0, default);
        Assert.AreEqual(1, events.Count(item => item.Type == FlowRunEventType.ChildFlowRunCreated));
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
        var child = await runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, root.Value.Id,
            "analyze", input.RootElement, "flowCall", null, default);

        var failure = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, child.Value.Id,
                "analyze", input.RootElement, "flowCall", null, default));

        Assert.AreEqual("flow_nesting_depth_exceeded", failure.Code);
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
        _ = await runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, root.Value.Id,
            "first", input.RootElement, "flowCall", null, default);

        var failure = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            runs.CreateOrGetAssignedChildAsync(TestScope.WorkspaceId, root.Value.Id,
                "second", input.RootElement, "flowCall", null, default));

        Assert.AreEqual("flow_descendant_limit_exceeded", failure.Code);
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
    }

    private static FlowRunService Service(
        FlowFixture fixture,
        TestFlowRunQueue queue,
        FlowRunExecutionOptions? options = null,
        IFlowStepArtifactCapture? artifactCapture = null)
    {
        return new FlowRunService(
            fixture.Repository,
            queue,
            new TestCancellationRegistry(),
            new NullFlowRunEventSink(),
            TimeProvider.System,
            options,
            configuredArtifactCapture: artifactCapture);
    }

    private static async Task<Agentstration.Flows.Storage.Abstractions.StoredFlow> CreatePublishedGraphAsync(
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
            new OutputFlowStepDefinition { Name = "failure", Outcome = FlowOutputOutcome.Error, Code = "CHILD_FLOW_FAILED", Message = "Child Flow failed." }
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
