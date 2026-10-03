using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void RepeatRoundTripsThroughJsonAndYamlAndContributesToTheDefinitionHash()
    {
        var graph = RepeatGraph(3);

        var json = JsonSerializer.Serialize(graph, JsonOptions);
        var restored = JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions)!;
        var yaml = FlowDraftService.ToYaml(graph);
        var yamlRestored = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");

        StringAssert.Contains(json, "\"type\":\"repeat\"");
        var repeat = Assert.IsInstanceOfType<RepeatFlowStepDefinition>(restored.Steps[1]);
        Assert.AreEqual(3, repeat.MaximumIterations);
        Assert.AreEqual("${steps.read.output.endOfContent == true}", repeat.Until);
        Assert.AreEqual(
            FlowDefinitionHash.Compute(graph),
            FlowDefinitionHash.Compute(yamlRestored),
            $"{json}{Environment.NewLine}{JsonSerializer.Serialize(yamlRestored, JsonOptions)}");
        Assert.AreNotEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(RepeatGraph(4)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public async Task RepeatValidationRejectsAnUnsafeIterationLimit(int maximumIterations)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { offset = new { type = "integer" } },
            required = new[] { "offset" }
        });
        var resolver = new FlowCallResolverStub(new(new("chunk-reader"), "1.0.0", schema, JsonSerializer.SerializeToElement(new { type = "object" })));

        var result = await new FlowGraphValidator(resolver).ValidateAsync(
            RepeatGraph(maximumIterations),
            new FlowValidationContext(true, TestScope.WorkspaceId, new("parent")),
            default);

        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_repeat_iterations_invalid"));
    }

    [TestMethod]
    public async Task RepositoryResolverDetectsDependencyCyclesThroughRepeatSteps()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var parent = await CreatePublishedGraphAsync(fixture, "parent", FlowDraftTemplates.Create("empty"));
        var child = await CreatePublishedGraphAsync(fixture, "child", RepeatGraph(1, "parent"));
        var resolver = new ManagementFlowResourceReferenceResolver(null!, fixture.Repository);
        var target = await resolver.ResolveFlowAsync(TestScope.WorkspaceId, parent.Value.Id.Namespace,
            new(child.Value.Id.Value, FlowCallVersionStrategy.Exact, "1.0.0"), default);

        Assert.IsNotNull(target);
        Assert.IsTrue(await resolver.CreatesFlowCycleAsync(TestScope.WorkspaceId, parent.Value.Id, target, default));
    }

    [TestMethod]
    public async Task RepeatCreatesDurableChildrenUntilTheConditionSucceeds()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "chunk-reader", RepeatChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", RepeatGraph(5));
        var runs = Service(fixture, new TestFlowRunQueue(), agents: new ChunkAgentExecutor(endOffset: 3));
        var input = JsonSerializer.SerializeToElement(new { source = "docs", offset = 0 });
        var pending = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "repeat-success", input, TestScope, default);

        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var firstWaiting = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        var firstChildId = firstWaiting.Steps.Single(step => step.StepName == "read").ChildFlowRunId!;

        await runs.ExecuteAsync(new(firstWaiting.Id, TestScope), default);
        var redelivered = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(firstChildId, redelivered.Steps.Single(step => step.StepName == "read").ChildFlowRunId);
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            var parentRun = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
            var repeat = parentRun.Steps.Single(step => step.StepName == "read");
            Assert.AreEqual(iteration, repeat.RepeatIteration);
            await runs.ExecuteAsync(new(repeat.ChildFlowRunId!, TestScope), default);
            await runs.ExecuteAsync(new(parentRun.Id, TestScope), default);
        }

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual(3, completed.Output?.GetProperty("nextOffset").GetInt32());
        var completedRepeat = completed.Steps.Single(step => step.StepName == "read");
        Assert.AreEqual(3, completedRepeat.RepeatIteration);
        Assert.HasCount(3, completedRepeat.ChildFlowRunIds);
        Assert.AreEqual(3, completedRepeat.ChildFlowRunIds.Distinct(StringComparer.Ordinal).Count());

        var causality = await runs.GetCausalityAsync(completed.Id, 0, 10, TestScope, default);
        Assert.AreEqual(4, causality.TotalCount);
        Assert.AreEqual(3, causality.Items.Count(item => item.ParentStepName == "read"));
    }

    [TestMethod]
    public async Task RepeatFailsWhenTheIterationLimitIsReached()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "chunk-reader", RepeatChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", RepeatGraph(2));
        var runs = Service(fixture, new TestFlowRunQueue(), agents: new ChunkAgentExecutor(endOffset: 10));
        var pending = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "repeat-limit", JsonSerializer.SerializeToElement(new { source = "docs", offset = 0 }), TestScope, default);

        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        for (var iteration = 1; iteration <= 2; iteration++)
        {
            var parentRun = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
            var childRunId = parentRun.Steps.Single(step => step.StepName == "read").ChildFlowRunId!;
            await runs.ExecuteAsync(new(childRunId, TestScope), default);
            await runs.ExecuteAsync(new(parentRun.Id, TestScope), default);
        }

        var failed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failed.Status);
        Assert.AreEqual("flow_repeat_limit_exceeded", failed.Error?.Code);
        Assert.HasCount(2, failed.Steps.Single(step => step.StepName == "read").ChildFlowRunIds);
    }

    [TestMethod]
    public async Task RepeatRecoveryRecreatesTheCurrentChildWithItsPersistedInput()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "chunk-reader", RepeatChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", RepeatGraph(5));
        var first = Service(fixture, new TestFlowRunQueue(), agents: new ChunkAgentExecutor(endOffset: 3));
        var pending = await first.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "repeat-recovery", JsonSerializer.SerializeToElement(new { source = "docs", offset = 0 }), TestScope, default);

        await first.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var firstWaiting = (await first.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        await first.ExecuteAsync(new(firstWaiting.Steps.Single(step => step.StepName == "read").ChildFlowRunId!, TestScope), default);
        await first.ExecuteAsync(new(firstWaiting.Id, TestScope), default);

        var secondWaiting = (await first.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        var repeat = secondWaiting.Steps.Single(step => step.StepName == "read");
        Assert.AreEqual(2, repeat.RepeatIteration);
        Assert.AreEqual(1, repeat.ResolvedInput?.GetProperty("offset").GetInt32());
        var missingChildId = repeat.ChildFlowRunId!;
        var missingChild = (await first.GetAsync(TestScope.WorkspaceId, missingChildId, default))!;
        await fixture.Repository.DeleteRunAsync(TestScope.WorkspaceId, missingChildId, missingChild.ETag, default);

        var recoveryQueue = new TestFlowRunQueue();
        var recovered = Service(fixture, recoveryQueue, agents: new ChunkAgentExecutor(endOffset: 3));
        await recovered.InitializeAsync(default);
        Assert.IsTrue(recoveryQueue.Enqueued.Any(item => item.RunId == pending.Value.Id));

        await recovered.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var recreated = (await recovered.GetAsync(TestScope.WorkspaceId, missingChildId, default))!.Value;
        Assert.AreEqual(1, recreated.Input.GetProperty("offset").GetInt32());
        Assert.AreEqual(pending.Value.Id, recreated.ParentFlowRunId);
    }

    private static FlowGraphDefinition RepeatGraph(int maximumIterations, string childFlowName = "chunk-reader") => new()
    {
        EntryStep = "input",
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new RepeatFlowStepDefinition
            {
                Name = "read",
                Flow = new(childFlowName, FlowCallVersionStrategy.Exact, "1.0.0"),
                InputMapping = JsonSerializer.SerializeToElement(new { offset = "${input.offset}" }),
                NextInputMapping = JsonSerializer.SerializeToElement(new { offset = "${steps.read.output.nextOffset}" }),
                Until = "${steps.read.output.endOfContent == true}",
                MaximumIterations = maximumIterations
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") }
        ],
        Transitions =
        [
            new("input-read", "input", "completed", "read"),
            new("read-output", "read", "completed", "output")
        ]
    };

    private static FlowGraphDefinition RepeatChildGraph() => new()
    {
        EntryStep = "input",
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { offset = new { type = "integer" } },
            required = new[] { "offset" }
        }),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new AgentFlowStepDefinition { Name = "read", Agent = new("chunk-agent"), InputMapping = JsonSerializer.SerializeToElement("${input}") },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") }
        ],
        Transitions =
        [
            new("input-read", "input", "completed", "read"),
            new("read-output", "read", "completed", "output")
        ]
    };

    private sealed class ChunkAgentExecutor(int endOffset) : IFlowAgentExecutor
    {
        public Task<FlowAgentExecutionResult> ExecuteAsync(
            FlowAgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextOffset = request.Input.GetProperty("offset").GetInt32() + 1;
            return Task.FromResult(new FlowAgentExecutionResult(
                JsonSerializer.SerializeToElement(new { nextOffset, endOfContent = nextOffset >= endOffset }),
                "/agents/chunk-agent",
                1,
                "/profiles/default",
                "Deterministic",
                null,
                [],
                []));
        }
    }
}
