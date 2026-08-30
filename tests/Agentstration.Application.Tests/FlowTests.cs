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
public sealed class FlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

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

    private static void AssertValidationCode(string code, FlowDefinition definition)
    {
        var exception = Assert.ThrowsExactly<FlowValidationException>(() => FlowValidator.Validate(Definition($"invalid-{code}", definition)));
        Assert.AreEqual(code, exception.Code);
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
    public async Task FlowServiceIsolatesHomonymousFlowsAndResolvesRelativeReferencesWithinOwnerNamespace()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        var command = new CreateFlowCommand("router", "Routes work", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant")));
        var first = await fixture.Service.CreateAsync(TestScope.WorkspaceId, command, firstNamespace, default);
        var second = await fixture.Service.CreateAsync(TestScope.WorkspaceId, command, secondNamespace, default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, first.Value.Id, "1.0.0", true, default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, second.Value.Id, "1.0.0", true, default);

        Assert.AreEqual(firstNamespace, (await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router", firstNamespace), default))?.Value.Id.Namespace);
        Assert.AreEqual(secondNamespace, (await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router", secondNamespace), default))?.Value.Id.Namespace);
        Assert.IsNull(await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router"), default));
        Assert.AreEqual(firstNamespace, (await fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(new FlowId("router")), firstNamespace, default)).FlowId.Namespace);
        Assert.AreEqual(secondNamespace, (await fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(new FlowId("router")), secondNamespace, default)).FlowId.Namespace);
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
    public async Task FlowApiSupportsCrudVersionsPolymorphismAndOpenApi()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var request = new CreateFlowRequest("direct-sql", "Direct SQL work", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        using var createdResponse = await client.PostAsJsonAsync("/api/flows", request, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.IsInstanceOfType<DirectFlowDefinition>(created!.Definition);

        using var versionResponse = await client.PostAsJsonAsync("/api/flows/direct-sql/versions", new CreateFlowVersionRequest("1.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, versionResponse.StatusCode);
        var version = await client.GetFromJsonAsync<FlowVersionResponse>("/api/flows/direct-sql/versions/1.0.0", JsonOptions);
        Assert.AreEqual("1.0.0", version!.Version);

        var get = await client.GetAsync("/api/flows/direct-sql");
        var etag = get.Headers.ETag!.Tag;
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/flows/direct-sql")
        {
            Content = JsonContent.Create(new UpdateFlowRequest("Updated", "1.1.0", true, request.Definition), options: JsonOptions)
        };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var list = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows");
        Assert.IsTrue(list!.Value.Any(value => value.Id == "direct-sql"));

        var openApi = await client.GetStringAsync("/openapi/v1.json");
        StringAssert.Contains(openApi, "flowKind");
        StringAssert.Contains(openApi, "DirectFlowDefinition");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/flows/direct-sql");
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/flows/direct-sql")).StatusCode);
    }

    [TestMethod]
    public async Task FlowApiAddressesHomonymousFlowsThroughNamespaceRoutes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var first = new CreateFlowRequest("router", null, "1.0.0", true, definition) { Namespace = firstNamespace };
        var second = first with { Namespace = secondNamespace };

        using var firstResponse = await client.PostAsJsonAsync("/api/namespaces/team-a/flows/", first, JsonOptions);
        using var secondResponse = await client.PostAsJsonAsync("/api/namespaces/team-b/flows/", second, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, firstResponse.StatusCode, await firstResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, secondResponse.StatusCode, await secondResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/flows/router")).StatusCode);

        var firstStored = await client.GetFromJsonAsync<FlowResponse>("/api/namespaces/team-a/flows/router", JsonOptions);
        var secondStored = await client.GetFromJsonAsync<FlowResponse>("/api/namespaces/team-b/flows/router", JsonOptions);
        Assert.AreEqual(firstNamespace, firstStored?.Namespace);
        Assert.AreEqual(secondNamespace, secondStored?.Namespace);
        var firstPage = await client.GetFromJsonAsync<FlowPageResponse>("/api/namespaces/team-a/flows", JsonOptions);
        Assert.IsTrue(firstPage?.Value.All(flow => flow.Namespace == firstNamespace));
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
    public async Task FlowRunsAreIsolatedByTheirDurableWorkspaceScope()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("scoped-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressions, expressions,
            new NullFlowRunEventSink(), new TestFlowRunExecutionScope(), TimeProvider.System);
        var otherScope = TestScope with { WorkspaceId = new(Guid.Parse("44444444-4444-4444-4444-444444444444")) };
        var otherFlow = await fixture.Service.CreateAsync(otherScope.WorkspaceId, new CreateFlowCommand("scoped-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(otherScope.WorkspaceId, otherFlow.Value.Id, "1.0.0", true, default);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");

        var own = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "own", input.RootElement, TestScope, default);
        var other = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "other", input.RootElement, otherScope, default);

        Assert.IsNotNull(await runs.GetAsync(own.Value.Id, TestScope, default));
        Assert.IsNull(await runs.GetAsync(other.Value.Id, TestScope, default));
        var page = await runs.ListAsync(null, null, 0, 20, TestScope, default);
        CollectionAssert.AreEqual(new[] { own.Value.Id }, page.Items.Select(item => item.Value.Id).ToArray());
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.CancelAsync(other.Value.Id, TestScope, default));
    }

    [TestMethod]
    public async Task FlowRunAuthorizationIsRevalidatedBeforeExecution()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("revoked-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var agent = new TrackingAgentExecutor();
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            agent, new UnsupportedFlowOrchestrationEngine(), expressions, expressions,
            new NullFlowRunEventSink(), new DeniedFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "revoked", input.RootElement, TestScope, default);

        await runs.ExecuteAsync(new(pending.Value.Id, TestScope), default);

        var failed = (await runs.GetAsync(TestScope.WorkspaceId, pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Failed, failed.Status);
        Assert.AreEqual("flow_run_authorization_denied", failed.Error?.Code);
        Assert.AreEqual(0, agent.ExecutionCount);
    }

    [TestMethod]
    public async Task FlowRunExecutionScopeCannotBeChangedAfterCreation()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("immutable-scope", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var expressions = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressions, expressions,
            new NullFlowRunEventSink(), new TestFlowRunExecutionScope(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "immutable", input.RootElement, TestScope, default);
        var changedScope = TestScope with { PrincipalId = Guid.Parse("55555555-5555-5555-5555-555555555555") };

        await Assert.ThrowsExactlyAsync<FlowConcurrencyException>(() => fixture.Repository.UpdateRunAsync(
            pending.Value with { Scope = changedScope }, pending.ETag, default));
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

    [TestMethod]
    public async Task FlowRunApiCreatesAndListsRunsFromTheSameContract()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("api-run-flow", "API run", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows/api-run-flow/versions", new CreateFlowVersionRequest("1.0.0"))).StatusCode);
        using var input = JsonDocument.Parse("""{"prompt":"Explain SQL joins"}""");
        using var createdResponse = await client.PostAsJsonAsync("/api/flows/api-run-flow/runs",
            new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var run = await createdResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.IsNotNull(run);
        Assert.AreEqual("1.0.0", run.FlowVersion);
        Assert.AreEqual(3, run.Steps.Count);
        var requestContext = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        Assert.AreEqual(new FlowRunScope(requestContext.TenantId, new(requestContext.WorkspaceId), requestContext.PrincipalId), run.Scope);
        var principal = await factory.Services.GetRequiredService<IIdentityStore>().GetPrincipalAsync(requestContext.PrincipalId, default);
        Assert.AreEqual(principal?.DisplayName, run.StartedBy);
        Assert.IsNull(typeof(CreateFlowRunRequest).GetProperty("StartedBy"));
        var global = await client.GetFromJsonAsync<FlowRunPageResponse>("/api/flowRuns", JsonOptions);
        Assert.IsTrue(global!.Value.Any(item => item.Id == run.Id));
        var scoped = await client.GetFromJsonAsync<FlowRunPageResponse>("/api/flows/api-run-flow/runs", JsonOptions);
        Assert.IsTrue(scoped!.Value.Any(item => item.Id == run.Id));
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Contains("/api/flowRuns/{runId}", routes);
        Assert.DoesNotContain("/flowRuns/{runId}", routes);
    }

    [TestMethod]
    public async Task FlowRunInputApiSupportsEveryInteractionTypeConflictAndExpiration()
    {
        var clock = new AdvancingTimeProvider(new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero));
        var queue = new TestFlowRunQueue();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlowRunQueue>();
                services.RemoveAll<IFlowOrchestrationEngine>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<IFlowRunQueue>(queue);
                services.AddSingleton<IFlowOrchestrationEngine, TypedSuspendingOrchestrationEngine>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("interactive-api-flow", "Interactive API", "1.0.0", true,
            new OrchestrationFlowDefinition(
                [
                    new FlowTargetReference(FlowTargetKind.Agent, "sql-expert"),
                    new FlowTargetReference(FlowTargetKind.Agent, "dotnet-expert")
                ],
                new SequentialOrchestrationPattern()));
        Assert.AreEqual(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/flows/interactive-api-flow/versions", new CreateFlowVersionRequest("1.0.0"))).StatusCode);
        var service = factory.Services.GetRequiredService<FlowRunService>();
        var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var principalId = current.PrincipalId.ToString("D");
        var apiScope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId);

        var cases = new[]
        {
            new InteractionApiCase("text", InputRequestType.Text, Array.Empty<string>(), JsonSerializer.SerializeToElement("Ada"), JsonSerializer.SerializeToElement("")),
            new InteractionApiCase("choice", InputRequestType.Choice, new[] { "red", "blue" }, JsonSerializer.SerializeToElement("blue"), JsonSerializer.SerializeToElement("green")),
            new InteractionApiCase("confirmation", InputRequestType.Confirmation, Array.Empty<string>(), JsonSerializer.SerializeToElement(true), JsonSerializer.SerializeToElement("yes"))
        };

        foreach (var interaction in cases)
        {
            using var input = JsonDocument.Parse($$"""{"kind":"{{interaction.Kind}}"}""");
            using var createResponse = await client.PostAsJsonAsync("/api/flows/interactive-api-flow/runs",
                new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Accepted, createResponse.StatusCode);
            var run = await createResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
            Assert.IsNotNull(run);
            await service.ExecuteAsync(new(run.Id, apiScope), default);

            var pending = await client.GetFromJsonAsync<InputRequest[]>(
                $"/api/flowRuns/{run.Id}/inputs?status=Pending", JsonOptions);
            Assert.IsNotNull(pending);
            Assert.HasCount(1, pending);
            var request = pending[0];
            Assert.AreEqual(interaction.Type, request.Type);
            CollectionAssert.AreEqual(interaction.Options.ToArray(), request.Options.ToArray());
            using var detailResponse = await client.GetAsync($"/api/flowRuns/{run.Id}/inputs/{request.Id}");
            Assert.AreEqual(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.IsNotNull(detailResponse.Headers.ETag);

            using var invalidResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.InvalidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            using var acceptedResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.ValidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
            var answered = await acceptedResponse.Content.ReadFromJsonAsync<InputRequest>(JsonOptions);
            Assert.AreEqual(InputRequestStatus.Answered, answered!.Status);
            Assert.AreEqual(principalId, answered.Response!.PrincipalId);

            using var duplicateResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.ValidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        }

        using var expiringInput = JsonDocument.Parse("""{"kind":"text"}""");
        var expiringCreate = await client.PostAsJsonAsync("/api/flows/interactive-api-flow/runs",
            new CreateFlowRunRequest(expiringInput.RootElement.Clone()), JsonOptions);
        var expiringRun = await expiringCreate.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        await service.ExecuteAsync(new(expiringRun!.Id, apiScope), default);
        var expiringRequest = (await client.GetFromJsonAsync<InputRequest[]>(
            $"/api/flowRuns/{expiringRun.Id}/inputs?status=Pending", JsonOptions))!.Single();
        clock.Advance(TimeSpan.FromDays(8));

        using var expiredResponse = await client.PostAsJsonAsync(
            $"/api/flowRuns/{expiringRun.Id}/inputs/{expiringRequest.Id}/response",
            new SubmitInputResponseRequest(JsonSerializer.SerializeToElement("too late")), JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, expiredResponse.StatusCode);
        var expired = await client.GetFromJsonAsync<InputRequest>(
            $"/api/flowRuns/{expiringRun.Id}/inputs/{expiringRequest.Id}", JsonOptions);
        Assert.AreEqual(InputRequestStatus.Expired, expired!.Status);
        var timedOut = await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{expiringRun.Id}", JsonOptions);
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut!.Status);
    }

    [TestMethod]
    public async Task FlowRunPaginationPreservesRouteNamespaceAndFiltersAcrossPages()
    {
        var queue = new TestFlowRunQueue();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlowRunQueue>();
                services.AddSingleton<IFlowRunQueue>(queue);
            });
        });
        using var client = factory.CreateClient();
        var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var scope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var defaultFlow = await flows.CreateAsync(scope.WorkspaceId, new CreateFlowCommand("paged-default", null, "1.0.0", true, definition), default);
        var namespacedFlow = await flows.CreateAsync(scope.WorkspaceId, new CreateFlowCommand("paged-namespaced", null, "1.0.0", true, definition), new ResourceNamespace("team-a"), default);
        await flows.PublishVersionAsync(scope.WorkspaceId, defaultFlow.Value.Id, "1.0.0", true, default);
        await flows.PublishVersionAsync(scope.WorkspaceId, namespacedFlow.Value.Id, "1.0.0", true, default);
        using var input = JsonDocument.Parse("{}");
        for (var index = 0; index < 3; index++)
        {
            await runs.CreateAsync(defaultFlow.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", $"default-{index}", input.RootElement, scope, default);
            await runs.CreateAsync(namespacedFlow.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", $"namespaced-{index}", input.RootElement, scope, default);
        }

        var namespaced = await client.GetFromJsonAsync<FlowRunPageResponse>(
            "/api/namespaces/team-a/flows/paged-namespaced/runs?status=Pending&top=1", JsonOptions);
        Assert.IsNotNull(namespaced?.NextLink);
        StringAssert.StartsWith(namespaced.NextLink, "/api/namespaces/team-a/flows/paged-namespaced/runs?");
        StringAssert.Contains(namespaced.NextLink, "status=Pending");
        var namespacedIds = new List<string>(namespaced.Value.Select(value => value.Id));
        var next = namespaced.NextLink;
        while (next is not null)
        {
            var page = await client.GetFromJsonAsync<FlowRunPageResponse>(next, JsonOptions);
            Assert.IsNotNull(page);
            namespacedIds.AddRange(page.Value.Select(value => value.Id));
            next = page.NextLink;
        }
        Assert.HasCount(3, namespacedIds);

        var global = await client.GetFromJsonAsync<FlowRunPageResponse>(
            "/api/flowRuns?flowId=paged-default&status=Pending&top=1", JsonOptions);
        Assert.IsNotNull(global?.NextLink);
        StringAssert.StartsWith(global.NextLink, "/api/flowRuns?");
        StringAssert.Contains(global.NextLink, "flowId=paged-default");
        StringAssert.Contains(global.NextLink, "status=Pending");
    }

    [TestMethod]
    public void FlowRunConsoleUsesDistinctRouteFromApi()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToArray();

        Assert.Contains("/flow-runs", routes);
        Assert.Contains("/flow-runs/{RunId}", routes);
        Assert.Contains("/api/flowRuns/{runId}", routes);
        Assert.DoesNotContain("/flowRuns/{runId}", routes);
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
    public async Task DraftApiValidatesInputRunsImmutableSnapshotPublishesAndRoundTripsYaml()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        using var createdResponse = await client.PostAsJsonAsync("/api/flows/drafts",
            new CreateFlowDraftRequest("designer-api-flow", "Designer API Flow", Template: "AgentRouting"), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var draft = await createdResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.IsNotNull(draft);
        Assert.AreEqual(1L, draft.Value.Revision);
        Assert.AreEqual(7, Enum.GetValues<FlowNodeKind>().Count(kind => kind is FlowNodeKind.Input or FlowNodeKind.Agent or FlowNodeKind.Router or FlowNodeKind.Condition or FlowNodeKind.Transform or FlowNodeKind.Output or FlowNodeKind.Failure));

        using var validationResponse = await client.PostAsync("/api/flows/designer-api-flow/validate", null);
        Assert.AreEqual(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = await validationResponse.Content.ReadFromJsonAsync<FlowValidationResponse>(JsonOptions);
        Assert.IsTrue(validation!.IsValid, string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));

        var source = await client.GetFromJsonAsync<FlowSourceResponse>("/api/flows/designer-api-flow/draft/source?format=yaml", JsonOptions);
        Assert.IsNotNull(source);
        StringAssert.Contains(source.Source, "entryStep:");
        StringAssert.Contains(source.Source, "type: router");
        using var replaceSource = new HttpRequestMessage(HttpMethod.Put, "/api/flows/designer-api-flow/draft/source")
        {
            Content = JsonContent.Create(new ReplaceFlowSourceRequest(source.Source, "yaml", "source-test"), options: JsonOptions)
        };
        replaceSource.Headers.TryAddWithoutValidation("If-Match", draft.ETag);
        using var replacedResponse = await client.SendAsync(replaceSource);
        Assert.AreEqual(HttpStatusCode.OK, replacedResponse.StatusCode);
        var replaced = await replacedResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.AreEqual(2L, replaced!.Value.Revision);
        using var staleSource = new HttpRequestMessage(HttpMethod.Put, "/api/flows/designer-api-flow/draft/source")
        {
            Content = JsonContent.Create(new ReplaceFlowSourceRequest(source.Source, "yaml", "source-test"), options: JsonOptions)
        };
        staleSource.Headers.TryAddWithoutValidation("If-Match", draft.ETag);
        using var staleResponse = await client.SendAsync(staleSource);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);

        using var missingInput = JsonDocument.Parse("{}");
        using var rejectedRun = await client.PostAsJsonAsync("/api/flows/designer-api-flow/draft/runs", new CreateFlowRunRequest(missingInput.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejectedRun.StatusCode);

        using var input = JsonDocument.Parse("""{"prompt":"Review this SQL query"}""");
        using var acceptedRun = await client.PostAsJsonAsync("/api/flows/designer-api-flow/draft/runs", new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Accepted, acceptedRun.StatusCode);
        var run = await acceptedRun.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.AreEqual(FlowDefinitionState.Draft, run!.DefinitionState);
        Assert.AreEqual(2L, run.DraftRevision);
        Assert.IsFalse(string.IsNullOrWhiteSpace(run.DefinitionHash));
        Assert.IsFalse(string.IsNullOrWhiteSpace(run.DefinitionSnapshotId));

        using var publishResponse = await client.PostAsJsonAsync("/api/flows/designer-api-flow/publish", new PublishFlowDraftRequest("1.0.0", "Initial designer release"), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, publishResponse.StatusCode);
        var version = await publishResponse.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions);
        Assert.AreEqual("Initial designer release", version!.ReleaseNotes);
        Assert.IsNotNull(version.Graph);
        Assert.AreEqual(replaced.Value.DefinitionHash, version.DefinitionHash);
        var routing = Assert.IsInstanceOfType<RoutingFlowDefinition>(version.Definition);
        CollectionAssert.AreEqual(new[] { "sql-expert", "dotnet-expert" }, routing.Destinations.Select(target => target.Id).ToArray());
        Assert.AreEqual("dotnet-expert", routing.Fallback?.Id);

        var current = await client.GetFromJsonAsync<FlowResponse>("/api/flows/designer-api-flow", JsonOptions);
        Assert.IsNotNull(current);
        Assert.IsNotNull(current.Graph);
        Assert.AreEqual("input", current.Graph.EntryStep);
        Assert.AreEqual(version.Graph.Steps.Count, current.Graph.Steps.Count);

        using var recreateResponse = await client.PostAsync("/api/flows/designer-api-flow/versions/1.0.0/draft", null);
        Assert.AreEqual(HttpStatusCode.OK, recreateResponse.StatusCode);
        var recreated = await recreateResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.AreEqual(3L, recreated!.Value.Revision);
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
            Assert.AreEqual(FlowOrchestrationStrategy.Sequential, request.Definition.Strategy);
            Assert.IsTrue(request.Definition.Participants.All(participant =>
                participant.Namespace == new ResourceNamespace("daily-life-assistant")));
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FlowParticipantTurnStarted("researcher", 1);
            yield return new FlowParticipantDelta("researcher", "draft");
            yield return new FlowParticipantTurnCompleted("researcher", 1);
            var researcher = Participant("researcher", 1, "draft");
            yield return new FlowParticipantCompleted(researcher);
            yield return new FlowParticipantTurnStarted("reviewer", 2);
            yield return new FlowParticipantDelta("reviewer", "reviewed");
            yield return new FlowParticipantTurnCompleted("reviewer", 2);
            var reviewer = Participant("reviewer", 2, "reviewed");
            yield return new FlowParticipantCompleted(reviewer);
            yield return new FlowExecutionCompleted(new FlowOrchestrationResult(
                FlowOrchestrationStrategy.Sequential,
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

    private sealed class ExistingResourceResolver : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
