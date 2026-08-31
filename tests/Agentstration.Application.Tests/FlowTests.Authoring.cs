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
    public void RoutingWorkflowOrchestrationAndCompositeValidateStructure()
    {
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("routing",
            new RoutingFlowDefinition(FlowRoutingStrategy.Capabilities, []))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("workflow-entry",
            new WorkflowFlowDefinition("missing", [new FlowNode("start", FlowNodeKind.Function)], []))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("workflow-edge",
            new WorkflowFlowDefinition("start", [new FlowNode("start", FlowNodeKind.Function)], [new FlowEdge("start", "missing")]))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("orchestration",
            new OrchestrationFlowDefinition([], new SequentialOrchestrationPattern()))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("self",
            new CompositeFlowDefinition(FlowCompositionMode.Sequential, [new FlowReference(new FlowId("self"), "1.0.0", false)]))));
    }

    [TestMethod]
    public void OrchestrationValidationEnforcesBoundsAndHandoffReachability()
    {
        var participants = new[] { "agent-a", "agent-b", "agent-c" }
            .Select(id => new FlowTargetReference(FlowTargetKind.Agent, id))
            .ToArray();

        AssertValidationCode(
            "group_chat_iterations_invalid",
            new OrchestrationFlowDefinition(participants, new GroupChatOrchestrationPattern(FlowValidator.MaximumGroupChatIterations + 1)));
        AssertValidationCode(
            "handoff_participant_unreachable",
            new OrchestrationFlowDefinition(participants, new HandoffOrchestrationPattern(
                "agent-a",
                [new FlowHandoff("agent-a", "agent-b")])));
        AssertValidationCode(
            "handoff_route_duplicate",
            new OrchestrationFlowDefinition(participants[..2], new HandoffOrchestrationPattern(
                "agent-a",
                [new FlowHandoff("agent-a", "agent-b"), new FlowHandoff("agent-a", "agent-b")])));
        AssertValidationCode(
            "magentic_limits_invalid",
            new OrchestrationFlowDefinition(participants[..2], new MagenticOrchestrationPattern(
                new FlowTargetReference(FlowTargetKind.Agent, "manager"),
                MaximumRounds: FlowValidator.MaximumMagenticRounds + 1)));
        AssertValidationCode(
            "orchestration_participant_duplicate",
            new OrchestrationFlowDefinition([participants[0], participants[0]], new SequentialOrchestrationPattern()));
    }

    [TestMethod]
    public void EveryFlowDefinitionRoundTripsWithDiscriminator()
    {
        FlowDefinition[] definitions =
        [
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-a")),
            new RoutingFlowDefinition(FlowRoutingStrategy.Capabilities, [new FlowTargetReference(FlowTargetKind.Agent, "expert")]),
            new WorkflowFlowDefinition("start", [new FlowNode("start", FlowNodeKind.Function)], []),
            new OrchestrationFlowDefinition(
                [new FlowTargetReference(FlowTargetKind.Agent, "agent-a"), new FlowTargetReference(FlowTargetKind.Agent, "agent-b")],
                new ConcurrentOrchestrationPattern()),
            new CompositeFlowDefinition(FlowCompositionMode.Sequential, [new FlowReference(new FlowId("child"), "1.0.0", false)])
        ];

        foreach (var definition in definitions)
        {
            var json = JsonSerializer.Serialize(definition, JsonOptions);
            StringAssert.Contains(json, "flowKind");
            var restored = JsonSerializer.Deserialize<FlowDefinition>(json, JsonOptions);
            Assert.AreEqual(definition.GetType(), restored!.GetType());
        }
    }

    [TestMethod]
    public async Task FlowServicePersistsVersionsResolvesActiveAndEnforcesConcurrency()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("technical-router", "Routes work", "1.0.0", true,
            new RoutingFlowDefinition(FlowRoutingStrategy.Capabilities, [new FlowTargetReference(FlowTargetKind.Agent, "technical-expert")])), default);
        var published = await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var precise = await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", default);
        var resolved = await fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(created.Value.Id), default);

        Assert.AreEqual(JsonSerializer.Serialize(published.Value, JsonOptions), JsonSerializer.Serialize(precise!.Value, JsonOptions));
        Assert.AreEqual("1.0.0", resolved.Version);
        Assert.AreEqual("1.0.0", (await fixture.Service.GetAsync(TestScope.WorkspaceId, created.Value.Id, default))!.Value.ActiveVersion);
        await Assert.ThrowsAsync<FlowConcurrencyException>(() => fixture.Service.UpdateAsync(TestScope.WorkspaceId, created.Value.Id,
            new UpdateFlowCommand("Changed", "1.1.0", true, created.Value.Definition), "\"stale\"", default));
        await Assert.ThrowsAsync<FlowConcurrencyException>(() => fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default));
    }

    [TestMethod]
    public void WorkItemCanReferenceAnExactFlowVersionWithoutEmbeddingDefinition()
    {
        var reference = new FlowReference(new FlowId("technical-router"), "1.0.0", false);
        var item = WorkItem.Create(WorkItemId.New(), TestScope.WorkspaceId, "question", "Help me", Now, flow: reference);
        var restored = WorkItem.Restore(item.ToSnapshot());
        Assert.AreEqual(reference, restored.Flow);
        Assert.Throws<FlowValidationException>(() => WorkItem.Create(WorkItemId.New(), TestScope.WorkspaceId, "question", "Help", Now,
            flow: new FlowReference(new FlowId("technical-router"), "1.0.0", true)));
    }

    [TestMethod]
    public async Task FlowRunExecutesPublishedSnapshotAndPersistsDiagnosticSteps()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("routing-run", "Routes SQL", "1.0.0", true,
            new RoutingFlowDefinition(FlowRoutingStrategy.Deterministic,
            [
                new FlowTargetReference(FlowTargetKind.Agent, "dotnet-expert"),
                new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")
            ])), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var queue = new TestFlowRunQueue();
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(
            fixture.Repository,
            queue,
            new TestCancellationRegistry(),
            new TestAgentExecutor(),
            new UnsupportedFlowOrchestrationEngine(),
            expressions,
            expressions,
            new NullFlowRunEventSink(),
            new TestFlowRunExecutionScope(),
            TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Review this SQL query"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "correlation-1", input.RootElement, TestScope, default);
        Assert.AreEqual(FlowRunStatus.Pending, pending.Value.Status);
        Assert.AreEqual(pending.Value.Id, queue.Enqueued.Single().RunId);
        Assert.AreEqual(TestScope, queue.Enqueued.Single().Scope);

        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);
        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        Assert.AreEqual("1.0.0", completed.DefinitionSnapshot.Version);
        CollectionAssert.AreEqual(new[] { "Input", "Router", "Agent", "Output" }, completed.Steps.Select(step => step.StepName).ToArray());
        Assert.IsTrue(completed.Steps.All(step => step.Status == FlowStepRunStatus.Succeeded));
        Assert.AreEqual("sql-expert", completed.Steps.Single(step => step.StepName == "Router").SelectedTransition);
        var agent = completed.Steps.Single(step => step.StepName == "Agent");
        Assert.AreEqual(3L, agent.AgentVersion);
        Assert.AreEqual("Deterministic", agent.Provider);
        Assert.AreEqual(12, agent.Usage!.InputTokens);
        Assert.AreEqual("done", completed.Output!.Value.GetString());
        Assert.AreEqual(1, (await runs.ListAsync(created.Value.Id, FlowRunStatus.Succeeded, 0, 20, TestScope, default)).Items.Count);

        var current = await fixture.Service.GetAsync(TestScope.WorkspaceId, created.Value.Id, default);
        await fixture.Service.DeleteAsync(TestScope.WorkspaceId, created.Value.Id, current!.ETag, default);

        Assert.IsNull(await fixture.Service.GetAsync(TestScope.WorkspaceId, created.Value.Id, default));
        Assert.IsNull(await fixture.Repository.GetVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", default));
        Assert.IsNotNull(await fixture.Repository.GetRunAsync(TestScope.WorkspaceId, completed.Id, default));
        Assert.IsNotEmpty(await fixture.Repository.ListRunEventsAsync(TestScope.WorkspaceId, completed.Id, 0, default));
    }

    [TestMethod]
    public async Task TypedGraphValidationReportsStructuralAndExpressionIssues()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ConditionFlowStepDefinition { Name = "decision", Mode = "Advanced", Expression = "${system.now()}" },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions =
            [
                new("to-decision", "input", "completed", "decision"),
                new("cycle", "decision", "true", "input")
            ]
        };

        var result = await new FlowGraphValidator(new ExistingResourceResolver()).ValidateAsync(graph, new FlowValidationContext(false), default);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "expression_invalid" && issue.StepId == "decision"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_cycle_not_supported"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "step_unreachable" && issue.StepId == "output"));
    }

    [TestMethod]
    public async Task DraftRunExecutesTypedGraphAndPersistsDifferentialEvents()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition { Name = "transform", Mapping = JsonSerializer.SerializeToElement(new { prompt = "${input.prompt}", eligible = true }) },
                new ConditionFlowStepDefinition { Name = "condition", Left = "${steps.transform.output.eligible}", Operator = "equals", Right = "true" },
                new RouterFlowStepDefinition { Name = "router", Candidates = [new("sql", new("sql-expert"), Examples: ["query"])], Fallback = new("sql-expert") },
                new AgentFlowStepDefinition { Name = "agent", Agent = new("${steps.router.output.selectedAgent}"), InputMapping = JsonSerializer.SerializeToElement(new { prompt = "${steps.transform.output.prompt}" }) },
                new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement(new { result = "${steps.agent.output}" }) },
                new FailureFlowStepDefinition { Name = "failure" }
            ],
            Transitions =
            [
                new("t1", "input", "completed", "transform"),
                new("t2", "transform", "completed", "condition"),
                new("t3", "condition", "true", "router"),
                new("t4", "condition", "false", "failure"),
                new("t5", "router", "selected", "agent"),
                new("t6", "router", "failed", "failure"),
                new("t7", "agent", "completed", "output"),
                new("t8", "agent", "failed", "failure")
            ]
        };
        var now = TimeProvider.System.GetUtcNow();
        var draft = new FlowDraft { WorkspaceId = TestScope.WorkspaceId, Id = "typed-draft", FlowId = new("typed-run"), DisplayName = "Typed run", Definition = graph, CreatedAt = now, UpdatedAt = now };
        var queue = new TestFlowRunQueue();
        var expressionEngine = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(), new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressionEngine, expressionEngine, new NullFlowRunEventSink(), new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Review this query"}""");

        var pending = await runs.CreateDraftAsync(draft, FlowRunTrigger.Manual, "tester", "typed-correlation", input.RootElement, TestScope, default);
        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var completed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        CollectionAssert.AreEqual(new[] { "input", "transform", "condition", "router", "agent", "output" }, completed.Steps.Where(step => step.Status == FlowStepRunStatus.Succeeded).Select(step => step.StepName).ToArray());
        Assert.AreEqual(FlowStepRunStatus.Skipped, completed.Steps.Single(step => step.StepName == "failure").Status);
        Assert.AreEqual("done", completed.Output!.Value.GetProperty("result").GetString());
        var events = await runs.ListEventsAsync(TestScope, completed.Id, 0, default);
        Assert.IsTrue(events.Count >= 15);
        Assert.AreEqual(FlowRunEventType.FlowRunCreated, events[0].Type);
        Assert.AreEqual(FlowRunEventType.FlowRunCompleted, events[^1].Type);
        CollectionAssert.AreEqual(events.Select(item => item.Sequence).Order().ToArray(), events.Select(item => item.Sequence).ToArray());
    }

    [TestMethod]
    public async Task DraftSnapshotsPreserveRouterAndStaticAgentTargetsWithoutTreatingExpressionsAsAgents()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var queue = new TestFlowRunQueue();
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(), new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(), new TestFlowRunExecutionScope(), TimeProvider.System);

        var routed = await SnapshotAsync("routed", new FlowGraphDefinition
        {
            EntryStep = "router",
            Steps = [new RouterFlowStepDefinition
            {
                Name = "router",
                Candidates = [new("sql", new("sql-expert")), new("dotnet", new("dotnet-expert"))],
                Fallback = new("fallback-agent")
            }]
        });
        CollectionAssert.AreEqual(new[] { "sql-expert", "dotnet-expert" }, routed.Destinations.Select(target => target.Id).ToArray());
        Assert.AreEqual("fallback-agent", routed.Fallback?.Id);

        var staticAgent = await SnapshotAsync("static", new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps = [new AgentFlowStepDefinition { Name = "agent", Agent = new("sql-expert") }]
        });
        CollectionAssert.AreEqual(new[] { "sql-expert" }, staticAgent.Destinations.Select(target => target.Id).ToArray());

        var dynamicAgent = await SnapshotAsync("dynamic", new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps = [new AgentFlowStepDefinition { Name = "agent", Agent = new("${steps.router.output.selectedAgent}") }]
        });
        CollectionAssert.AreEqual(new[] { "unconfigured-agent" }, dynamicAgent.Destinations.Select(target => target.Id).ToArray());

        var noTarget = await SnapshotAsync("no-target", new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }]
        });
        CollectionAssert.AreEqual(new[] { "unconfigured-agent" }, noTarget.Destinations.Select(target => target.Id).ToArray());

        async Task<RoutingFlowDefinition> SnapshotAsync(string id, FlowGraphDefinition graph)
        {
            var now = TimeProvider.System.GetUtcNow();
            var draft = new FlowDraft { WorkspaceId = TestScope.WorkspaceId, Id = $"{id}-draft", FlowId = new(id), DisplayName = id, Definition = graph, CreatedAt = now, UpdatedAt = now };
            using var input = JsonDocument.Parse("{}");
            var pending = await runs.CreateDraftAsync(draft, FlowRunTrigger.Manual, "tester", id, input.RootElement, TestScope, default);
            return Assert.IsInstanceOfType<RoutingFlowDefinition>(pending.Value.DefinitionSnapshot.Definition);
        }
    }

    [TestMethod]
    public void EveryOrchestrationPatternRoundTripsWithoutRuntimeTypes()
    {
        FlowOrchestrationPattern[] patterns =
        [
            new SequentialOrchestrationPattern(),
            new ConcurrentOrchestrationPattern(),
            new HandoffOrchestrationPattern("agent-a", [new FlowHandoff("agent-a", "agent-b")]),
            new GroupChatOrchestrationPattern(),
            new MagenticOrchestrationPattern(new FlowTargetReference(FlowTargetKind.Agent, "manager"))
        ];

        foreach (var pattern in patterns)
        {
            var json = JsonSerializer.Serialize(pattern, JsonOptions);
            StringAssert.Contains(json, "strategy");
            Assert.IsFalse(json.Contains("Microsoft.Agents", StringComparison.Ordinal));
            var restored = JsonSerializer.Deserialize<FlowOrchestrationPattern>(json, JsonOptions);
            Assert.AreEqual(pattern.GetType(), restored!.GetType());
        }
    }

}

