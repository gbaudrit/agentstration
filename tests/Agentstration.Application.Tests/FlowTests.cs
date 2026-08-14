using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
        var created = await fixture.Service.CreateAsync(new CreateFlowCommand("technical-router", "Routes work", "1.0.0", true,
            new RoutingFlowDefinition(FlowRoutingStrategy.Capabilities, [new FlowTargetReference(FlowTargetKind.Agent, "technical-expert")])), default);
        var published = await fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default);
        var precise = await fixture.Service.GetVersionAsync(created.Value.Id, "1.0.0", default);
        var resolved = await fixture.Service.ResolveAsync(new FlowReference(created.Value.Id), default);

        Assert.AreEqual(JsonSerializer.Serialize(published.Value, JsonOptions), JsonSerializer.Serialize(precise!.Value, JsonOptions));
        Assert.AreEqual("1.0.0", resolved.Version);
        Assert.AreEqual("1.0.0", (await fixture.Service.GetAsync(created.Value.Id, default))!.Value.ActiveVersion);
        await Assert.ThrowsAsync<FlowConcurrencyException>(() => fixture.Service.UpdateAsync(created.Value.Id,
            new UpdateFlowCommand("Changed", "1.1.0", true, created.Value.Definition), "\"stale\"", default));
        await Assert.ThrowsAsync<FlowConcurrencyException>(() => fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default));
    }

    [TestMethod]
    public void WorkItemCanReferenceAnExactFlowVersionWithoutEmbeddingDefinition()
    {
        var reference = new FlowReference(new FlowId("technical-router"), "1.0.0", false);
        var item = WorkItem.Create(WorkItemId.New(), "question", "Help me", Now, flow: reference);
        var restored = WorkItem.Restore(item.ToSnapshot());
        Assert.AreEqual(reference, restored.Flow);
        Assert.Throws<FlowValidationException>(() => WorkItem.Create(WorkItemId.New(), "question", "Help", Now,
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
    public async Task FlowRunExecutesPublishedSnapshotAndPersistsDiagnosticSteps()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(new CreateFlowCommand("routing-run", "Routes SQL", "1.0.0", true,
            new RoutingFlowDefinition(FlowRoutingStrategy.Deterministic,
            [
                new FlowTargetReference(FlowTargetKind.Agent, "dotnet-expert"),
                new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")
            ])), default);
        await fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default);
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
            TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Review this SQL query"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "correlation-1", input.RootElement, default);
        Assert.AreEqual(FlowRunStatus.Pending, pending.Value.Status);
        Assert.AreEqual(pending.Value.Id, queue.Enqueued.Single());

        await runs.ExecuteAsync(pending.Value.Id, default);
        var completed = (await runs.GetAsync(pending.Value.Id, default))!.Value;
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
        Assert.AreEqual(1, (await runs.ListAsync(created.Value.Id, FlowRunStatus.Succeeded, 0, 20, default)).Items.Count);
    }

    [TestMethod]
    public async Task OrchestrationFlowUsesNeutralEngineAndPersistsParticipantProgress()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(new CreateFlowCommand(
            "orchestration-run",
            "Coordinates agents",
            "1.0.0",
            true,
            new OrchestrationFlowDefinition(
                [
                    new FlowTargetReference(FlowTargetKind.Agent, "researcher"),
                    new FlowTargetReference(FlowTargetKind.Agent, "reviewer")
                ],
                new SequentialOrchestrationPattern())), default);
        await fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default);
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
            TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Investigate"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "orchestration-correlation", input.RootElement, default);
        await runs.ExecuteAsync(pending.Value.Id, default);

        var completed = (await runs.GetAsync(pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        CollectionAssert.AreEqual(
            new[] { "Input", "researcher", "reviewer", "Output" },
            completed.Steps.Select(step => step.StepName).ToArray());
        Assert.IsTrue(completed.Steps.All(step => step.Status == FlowStepRunStatus.Succeeded));
        Assert.AreEqual("reviewed", completed.Output!.Value.GetProperty("finalOutput").GetString());
        Assert.HasCount(2, completed.Output.Value.GetProperty("participants").EnumerateArray().ToArray());
        var events = await runs.ListEventsAsync(completed.Id, 0, default);
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.StepOutputDelta));
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.ParticipantTurnStarted));
        Assert.AreEqual(2, events.Count(item => item.Type == FlowRunEventType.ParticipantTurnCompleted));
    }

    [TestMethod]
    public async Task OrchestrationFlowTimeoutPersistsAnExplicitTerminalState()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(new CreateFlowCommand(
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
        await fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default);
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
            TimeProvider.System,
            new FlowRunExecutionOptions { OrchestrationTimeout = TimeSpan.FromMilliseconds(50) });
        using var input = JsonDocument.Parse("""{"prompt":"Wait"}""");

        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", "timeout-correlation", input.RootElement, default);
        await runs.ExecuteAsync(pending.Value.Id, default);

        var timedOut = (await runs.GetAsync(pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut.Status);
        Assert.AreEqual("flow_run_timed_out", timedOut.Error!.Code);
        Assert.IsTrue((await runs.ListEventsAsync(timedOut.Id, 0, default)).Any(item => item.Type == FlowRunEventType.FlowRunTimedOut));
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
            new CreateFlowRunRequest(input.RootElement.Clone(), StartedBy: "api-test"), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var run = await createdResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.IsNotNull(run);
        Assert.AreEqual("1.0.0", run.FlowVersion);
        Assert.AreEqual(3, run.Steps.Count);
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
    public async Task FlowRunConsoleUsesDistinctRouteFromApi()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        using var consoleResponse = await client.GetAsync("/flow-runs");
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToArray();

        Assert.AreEqual(HttpStatusCode.OK, consoleResponse.StatusCode);
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
        var draft = new FlowDraft { Id = "typed-draft", FlowId = new("typed-run"), DisplayName = "Typed run", Definition = graph, CreatedAt = now, UpdatedAt = now };
        var queue = new TestFlowRunQueue();
        var expressionEngine = new FlowExpressionParser();
        var runs = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(), new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressionEngine, expressionEngine, new NullFlowRunEventSink(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"Review this query"}""");

        var pending = await runs.CreateDraftAsync(draft, FlowRunTrigger.Manual, "tester", "typed-correlation", input.RootElement, default);
        await runs.ExecuteAsync(pending.Value.Id, default);

        var completed = (await runs.GetAsync(pending.Value.Id, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, completed.Status);
        CollectionAssert.AreEqual(new[] { "input", "transform", "condition", "router", "agent", "output" }, completed.Steps.Where(step => step.Status == FlowStepRunStatus.Succeeded).Select(step => step.StepName).ToArray());
        Assert.AreEqual(FlowStepRunStatus.Skipped, completed.Steps.Single(step => step.StepName == "failure").Status);
        Assert.AreEqual("done", completed.Output!.Value.GetProperty("result").GetString());
        var events = await runs.ListEventsAsync(completed.Id, 0, default);
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
        var runs = new FlowRunService(fixture.Repository, queue, new TestCancellationRegistry(), new TestAgentExecutor(), new UnsupportedFlowOrchestrationEngine(), expressions, expressions, new NullFlowRunEventSink(), TimeProvider.System);

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
            var draft = new FlowDraft { Id = $"{id}-draft", FlowId = new(id), DisplayName = id, Definition = graph, CreatedAt = now, UpdatedAt = now };
            using var input = JsonDocument.Parse("{}");
            var pending = await runs.CreateDraftAsync(draft, FlowRunTrigger.Manual, "tester", id, input.RootElement, default);
            return Assert.IsInstanceOfType<RoutingFlowDefinition>(pending.Value.DefinitionSnapshot.Definition);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static FlowResource Definition(string name, FlowDefinition definition) => new(new FlowId(name), name, null, "1.0.0", true, null, definition, new Dictionary<string, string>(), Now, Now);

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
        public List<string> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(string runId, CancellationToken cancellationToken) { Enqueued.Add(runId); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
    }

    private sealed class TestCancellationRegistry : IFlowRunCancellationRegistry
    {
        public CancellationToken Register(string runId, CancellationToken stoppingToken) => stoppingToken;
        public bool Cancel(string runId) => true;
        public void Complete(string runId) { }
    }

    private sealed class TestAgentExecutor : IFlowAgentExecutor
    {
        public Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(new FlowAgentExecutionResult(JsonSerializer.SerializeToElement("done"), $"/agents/{target.Id}", 3, "/profiles/default", "Deterministic", new FlowStepRunUsage(12, 4), ["lookup"], ["executed"]));
    }

    private sealed class TestOrchestrationEngine : IFlowOrchestrationEngine
    {
        public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
            FlowOrchestrationExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(FlowOrchestrationStrategy.Sequential, request.Definition.Strategy);
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
