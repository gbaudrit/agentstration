using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void ExecutionContextExpressionsAcceptOnlyDocumentedProperties()
    {
        var parser = new FlowExpressionParser();

        Assert.IsTrue(parser.Parse("${execution.flowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.rootFlowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.parentFlowRunId}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.stepName}").IsValid);
        Assert.IsTrue(parser.Parse("${execution.correlationId}").IsValid);

        var unknown = parser.Parse("${execution.workspaceId}");
        Assert.IsFalse(unknown.IsValid);
        StringAssert.Contains(unknown.Error, "unknown execution context property");
    }

    [TestMethod]
    public async Task FlowAuthoringRejectsUnknownExecutionContextProperties()
    {
        var graph = ExecutionContextGraph() with
        {
            Steps = ExecutionContextGraph().Steps.Select(step => step is TransformFlowStepDefinition transform
                ? transform with { Mapping = JsonSerializer.SerializeToElement(new { value = "${execution.workspaceId}" }) }
                : step).ToArray()
        };

        var result = await new FlowGraphValidator(null!).ValidateAsync(
            graph,
            new FlowValidationContext(ResolveResources: false),
            default);

        var issue = result.Issues.Single(value => value.Code == "expression_invalid");
        StringAssert.Contains(issue.Message, "unknown execution context property");
        Assert.AreEqual("capture", issue.StepId);
    }

    [TestMethod]
    public async Task RootFlowResolvesAuthoritativeExecutionContext()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var flow = await CreatePublishedGraphAsync(fixture, "execution-context-root", ExecutionContextGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        var pending = await runs.CreateAsync(flow.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "execution-context-root-correlation", JsonSerializer.SerializeToElement(new { }), TestScope, default);

        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual(completed.Id, completed.Output?.GetProperty("flowRunId").GetString());
        Assert.AreEqual(completed.Id, completed.Output?.GetProperty("rootFlowRunId").GetString());
        Assert.AreEqual(JsonValueKind.Null, completed.Output?.GetProperty("parentFlowRunId").ValueKind);
        Assert.AreEqual("capture", completed.Output?.GetProperty("stepName").GetString());
        Assert.AreEqual("execution-context-root-correlation", completed.Output?.GetProperty("correlationId").GetString());
    }

    [TestMethod]
    public async Task ChildFlowResolvesPersistedRootParentAndCurrentRunIdentitiesAfterResume()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "execution-context-child", ExecutionContextGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "execution-context-parent", ExecutionContextParentGraph());
        var first = Service(fixture, new TestFlowRunQueue());
        var pending = await first.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "execution-context-child-correlation", JsonSerializer.SerializeToElement(new { }), TestScope, default);

        await first.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var waiting = (await first.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        var childId = waiting.Steps.Single(step => step.StepName == "store").ChildFlowRunId!;

        var resumed = Service(fixture, new TestFlowRunQueue());
        await resumed.ExecuteAsync(new(childId, TestScope), default);
        await resumed.ExecuteAsync(new(waiting.Id, TestScope), default);

        var completed = (await resumed.GetAsync(TestScope.WorkspaceId, waiting.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual(childId, completed.Output?.GetProperty("flowRunId").GetString());
        Assert.AreEqual(waiting.Id, completed.Output?.GetProperty("rootFlowRunId").GetString());
        Assert.AreEqual(waiting.Id, completed.Output?.GetProperty("parentFlowRunId").GetString());
        Assert.AreEqual("capture", completed.Output?.GetProperty("stepName").GetString());
        Assert.AreEqual("execution-context-child-correlation", completed.Output?.GetProperty("correlationId").GetString());
    }

    private static FlowGraphDefinition ExecutionContextGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new TransformFlowStepDefinition
            {
                Name = "capture",
                Mapping = JsonSerializer.SerializeToElement(new
                {
                    flowRunId = "${execution.flowRunId}",
                    rootFlowRunId = "${execution.rootFlowRunId}",
                    parentFlowRunId = "${execution.parentFlowRunId}",
                    stepName = "${execution.stepName}",
                    correlationId = "${execution.correlationId}"
                })
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.capture.output}") }
        ],
        Transitions =
        [
            new("input-capture", "input", "completed", "capture"),
            new("capture-output", "capture", "completed", "output")
        ]
    };

    private static FlowGraphDefinition ExecutionContextParentGraph() => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new FlowCallStepDefinition
            {
                Name = "store",
                Flow = new("execution-context-child", FlowCallVersionStrategy.Exact, "1.0.0"),
                InputMapping = JsonSerializer.SerializeToElement(new
                {
                    producerFlowRunId = "${execution.flowRunId}",
                    correlationId = "${execution.correlationId}"
                })
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.store.output}") }
        ],
        Transitions =
        [
            new("input-store", "input", "completed", "store"),
            new("store-output", "store", "completed", "output")
        ]
    };
}
