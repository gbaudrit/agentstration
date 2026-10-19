using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task FlowConsoleClientMarksRootInvocationOrigin()
    {
        string? origin = null;
        var now = DateTimeOffset.UtcNow;
        var flowId = new FlowId("console-run");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var version = new FlowVersion(new WorkspaceId(Guid.NewGuid()), flowId, "1.0.0", null, definition, new Dictionary<string, string>(), now);
        var run = new FlowRun
        {
            WorkspaceId = version.WorkspaceId,
            Id = "flowrun-console",
            FlowId = flowId,
            FlowVersion = version.Version,
            Scope = new(Guid.NewGuid(), version.WorkspaceId, Guid.NewGuid()),
            Input = JsonSerializer.SerializeToElement(new { }),
            CreatedAt = now,
            DefinitionSnapshot = version
        };
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            origin = request.Headers.GetValues("X-Agentstration-Origin").Single();
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(run) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        _ = await new FlowApiClient(httpClient).CreateFlowRunAsync(flowId.Value,
            new CreateFlowRunRequest(JsonSerializer.SerializeToElement(new { })), default);

        Assert.AreEqual("Console", origin);
    }

    [TestMethod]
    public void FlowConsoleUrlPreservesTheResourceNamespace()
    {
        Assert.AreEqual("/flows/main", ConsoleResourceUrls.Flow(new FlowId("main")));
        Assert.AreEqual(
            "/namespaces/agentstration.daily-life-assistant/flows/main",
            ConsoleResourceUrls.Flow(new FlowId("main", new ResourceNamespace("agentstration.daily-life-assistant"))));
        Assert.AreEqual("/entries/main", ConsoleResourceUrls.Entry(new EntryId("main")));
        Assert.AreEqual(
            "/namespaces/agentstration.daily-life-assistant/entries/main",
            ConsoleResourceUrls.Entry(new EntryId("main", new ResourceNamespace("agentstration.daily-life-assistant"))));
    }

    [TestMethod]
    public async Task EntryResourcePickerLoadsFlowsFromCanonicalFlowApiInsteadOfWorkApi()
    {
        var requestedCatalogs = new List<string>();
        var workRequests = new List<string>();
        using var workClient = new HttpClient(new StubHandler(request =>
        {
            workRequests.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }))
        { BaseAddress = new Uri("http://work-api/") };
        using var flowCatalog = new HttpClient(new StubHandler(request =>
        {
            Assert.AreEqual("/api/resources", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(FlowResourceKinds.Flow, Uri.UnescapeDataString(request.RequestUri.Query.Replace("?kind=", "", StringComparison.Ordinal)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new ResourcePickerItem("universal-router", "Universal router", null, "1.0.0", "Active", FlowResourceKinds.Flow),
                    new ResourcePickerItem("my-flow", "My flow", null, "1.0.0", "Active", FlowResourceKinds.Flow)
                })
            };
        }))
        { BaseAddress = new Uri("http://flow-api/") };
        var factory = new StubHttpClientFactory(name =>
        {
            requestedCatalogs.Add(name);
            return flowCatalog;
        });
        var client = new EntryAdministrationApiClient(workClient, factory);

        var resources = await client.GetResourcesAsync(EntryBindingKind.Flow, CancellationToken.None);

        Assert.HasCount(2, resources);
        Assert.IsTrue(resources.Any(value => value.Name == "My flow"));
        CollectionAssert.AreEqual(new[] { EntryAdministrationApiClient.FlowResourceCatalogClient }, requestedCatalogs);
        Assert.IsEmpty(workRequests);
    }

    [TestMethod]
    public async Task ConsoleEntryClientUsesCanonicalConsoleDiscoveryAndOwnerWorkspaceInvocation()
    {
        var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var @namespace = new ResourceNamespace("agentstration.assistant");
        var requests = new List<(HttpMethod Method, string PathAndQuery)>();
        var now = DateTimeOffset.UtcNow;
        var interaction = new InteractionResponse(
            Guid.NewGuid(),
            workspaceId,
            "assistant",
            InteractionStatus.Active,
            now,
            now,
            new Dictionary<string, JsonElement>(),
            [],
            [],
            null,
            null,
            new RespondAction("Ready"),
            1);
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<EntryResponse>()) }
                : new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new EntrySubmissionResponse(interaction, new RespondAction("Ready"), null))
                };
        }))
        { BaseAddress = new Uri("http://work-api/") };
        IEntryAdministrationApiClient client = new EntryAdministrationApiClient(
            httpClient,
            new StubHttpClientFactory(_ => httpClient));

        _ = await client.GetConsoleEntriesAsync(default);
        _ = await client.SubmitConsoleEntryAsync(
            workspaceId,
            @namespace,
            "assistant",
            new CreateInteractionRequest(new Dictionary<string, JsonElement>
            {
                ["request"] = JsonSerializer.SerializeToElement("Hello")
            }),
            default);

        CollectionAssert.AreEqual(new[]
        {
            (HttpMethod.Get, "/api/entries?surface=Console"),
            (HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/namespaces/{@namespace.Value}/entries/assistant/interactions?surface=Console")
        }, requests);
    }

    [TestMethod]
    public async Task ConsoleEntryInteractionClientUsesOnlyOwnerWorkspaceWorkRoutes()
    {
        var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var interactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var taskId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var artifactId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var now = DateTimeOffset.UtcNow;
        var interaction = new InteractionResponse(interactionId, workspaceId, "assistant", InteractionStatus.Active, now, now, new Dictionary<string, JsonElement>(), [], [], null, taskId, null, 1);
        var task = new WorkTaskResponse(taskId, workspaceId, "assistant", interactionId, "Task", null, WorkTaskStatus.Running, now, now, null, [], [], [], null, null, new CreateTaskAction(new(taskId), "Task", null, "/tasks"), 1);
        var requests = new List<(HttpMethod Method, string Path)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            object body = request.RequestUri.AbsolutePath switch
            {
                var path when path.EndsWith("/interactions", StringComparison.Ordinal) => new InteractionPageResponse([interaction]),
                var path when path.EndsWith($"/interactions/{interactionId:D}", StringComparison.Ordinal) => interaction,
                var path when path.EndsWith("/messages", StringComparison.Ordinal) => Array.Empty<ConversationMessage>(),
                var path when path.EndsWith("/pending-actions", StringComparison.Ordinal) => Array.Empty<PendingActionContract>(),
                var path when path.EndsWith("/activities", StringComparison.Ordinal) => Array.Empty<WorkTaskActivity>(),
                var path when path.EndsWith("/results", StringComparison.Ordinal) => Array.Empty<WorkTaskResult>(),
                var path when path.EndsWith("/artifacts", StringComparison.Ordinal) => Array.Empty<WorkTaskArtifact>(),
                _ => task
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) };
        }))
        { BaseAddress = new Uri("http://work-api/") };
        var client = new ConsoleEntryInteractionApiClient(httpClient);

        _ = await client.ListInteractionsAsync(workspaceId, 50, default);
        _ = await client.GetInteractionAsync(workspaceId, interactionId, default);
        _ = await client.ListMessagesAsync(workspaceId, interactionId, default);
        _ = await client.ListPendingActionsAsync(workspaceId, interactionId, default);
        _ = await client.GetTaskAsync(workspaceId, taskId, default);
        _ = await client.ListActivitiesAsync(workspaceId, taskId, default);
        _ = await client.ListResultsAsync(workspaceId, taskId, default);
        _ = await client.ListArtifactsAsync(workspaceId, taskId, default);
        _ = await client.CancelTaskAsync(workspaceId, taskId, default);

        CollectionAssert.AreEqual(new[]
        {
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/interactions?take=50"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/interactions/{interactionId:D}"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/interactions/{interactionId:D}/messages"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/interactions/{interactionId:D}/pending-actions"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/tasks/{taskId:D}"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/activities"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/results"),
            (HttpMethod.Get, $"/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/artifacts"),
            (HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/cancel")
        }, requests);
        Assert.AreEqual(
            $"http://work-api/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/artifacts/{artifactId:D}/content",
            client.GetArtifactContentUri(workspaceId, taskId, artifactId).AbsoluteUri);
    }

    [TestMethod]
    public async Task FlowAuthoringClientPreservesETagAndPublishesImmutableVersion()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var definition = new OrchestrationFlowDefinition(
            [new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")],
            new SequentialOrchestrationPattern());
        var flow = new FlowResponse("review", "review", null, "0.1.0", true, null, definition, new Dictionary<string, string>(), now, now);
        var version = new FlowVersionResponse("review", "0.1.0", null, definition, new Dictionary<string, string>(), now);
        var requests = new List<(HttpMethod Method, string Path, string? IfMatch)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.IfMatch.FirstOrDefault()?.ToString()));
            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(version) };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
            response.Headers.ETag = new EntityTagHeaderValue(request.Method == HttpMethod.Put ? "\"v2\"" : "\"v1\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new FlowApiClient(httpClient);

        var snapshot = await client.GetFlowSnapshotAsync("review", default);
        var updated = await client.UpdateFlowAsync("review", new UpdateFlowRequest(null, "0.1.0", true, definition), snapshot.ETag, default);
        var published = await client.CreateFlowVersionAsync("review", new CreateFlowVersionRequest("0.1.0"), default);

        Assert.AreEqual("\"v2\"", updated.ETag);
        Assert.AreEqual("0.1.0", published.Version);
        CollectionAssert.AreEqual(new[]
        {
            (HttpMethod.Get, "/api/flows/review", (string?)null),
            (HttpMethod.Put, "/api/flows/review", "\"v1\""),
            (HttpMethod.Post, "/api/flows/review/versions", (string?)null)
        }, requests);
    }

    [TestMethod]
    public async Task FlowClientListsAndLoadsNamespacedFlows()
    {
        var now = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        var @namespace = new ResourceNamespace("agentstration.who-am-i");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "who-am-i-host"));
        var summary = new FlowSummaryResponse("who-am-i-game", "Who Am I?", null, FlowKind.Direct, "0.1.0", true, "0.1.0", now) { Namespace = @namespace };
        var flow = new FlowResponse(summary.Id, summary.Name, null, summary.Version, true, summary.ActiveVersion, definition, new Dictionary<string, string>(), now, now) { Namespace = @namespace };
        var version = new FlowVersionResponse(summary.Id, summary.Version, null, definition, new Dictionary<string, string>(), now) { Namespace = @namespace };
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/api/flows" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowPageResponse([summary], null)) },
                "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) },
                "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { version }) },
                "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions/0.1.0" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(version) },
                "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/runs" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowRunPageResponse([], null)) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new FlowApiClient(httpClient);

        var listed = await client.GetFlowsAsync(default);
        _ = await client.GetFlowAsync(@namespace, summary.Id, default);
        _ = await client.GetFlowVersionsAsync(@namespace, summary.Id, default);
        _ = await client.GetFlowVersionAsync(@namespace, summary.Id, summary.Version, default);
        _ = await client.GetFlowRunsAsync(@namespace, summary.Id, default);

        Assert.HasCount(1, listed);
        Assert.AreEqual(@namespace, listed[0].Namespace);
        Assert.AreEqual("/namespaces/agentstration.who-am-i/flows/who-am-i-game", listed[0].DetailsUrl);
        CollectionAssert.AreEqual(new[]
        {
            "/api/flows?allNamespaces=true&top=100",
            "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game",
            "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions",
            "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions/0.1.0",
            "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/runs?top=200"
        }, requests);
    }

    [TestMethod]
    public async Task FlowClientLoadsEveryRunPageAndRejectsRepeatedContinuationLinks()
    {
        var first = CreateFlowRun("run-1");
        var second = CreateFlowRun("run-2");
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            return path switch
            {
                "/api/flowRuns?top=200" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FlowRunPageResponse([first], "/api/flowRuns?skip=1&top=200"))
                },
                "/api/flowRuns?skip=1&top=200" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FlowRunPageResponse([second], null))
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new FlowApiClient(httpClient);

        var runs = await client.GetFlowRunsAsync((string?)null, default);

        CollectionAssert.AreEqual(new[] { "run-1", "run-2" }, runs.Select(value => value.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "/api/flowRuns?top=200", "/api/flowRuns?skip=1&top=200" }, requests);

        var repeatedRequests = 0;
        using var repeatedHttpClient = new HttpClient(new StubHandler(_ =>
        {
            repeatedRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new FlowRunPageResponse([first], "/api/flowRuns?top=200"))
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        await Assert.ThrowsExactlyAsync<AgentstrationApiException>(() =>
            new FlowApiClient(repeatedHttpClient).GetFlowRunsAsync((string?)null, default));
        Assert.AreEqual(1, repeatedRequests);
    }

    [TestMethod]
    public async Task FlowClientLoadsEveryCausalityPageWithoutDroppingDescendants()
    {
        var rootRun = CreateFlowRun("root");
        var root = new FlowRunCausalityNode(rootRun.Id, rootRun.FlowId, rootRun.FlowVersion, true, FlowDefinitionState.Published,
            FlowRunStatus.WaitingForChild, null, null, 0, rootRun.CreatedAt, null, null, null, [], []);
        var child = root with { FlowRunId = "child", ParentFlowRunId = root.FlowRunId, ParentStepName = "analyze", NestingDepth = 1 };
        var origin = new FlowRunCausalityOrigin(root.FlowRunId, FlowInvocationOrigin.Trigger, FlowRunTrigger.Event, "watcher", "news-42", "correlation-42", null);
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            return path switch
            {
                "/api/flowRuns/child/causality?top=100" => new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FlowRunCausalityPageResponse(origin, [root], 2, "/api/flowRuns/child/causality?skip=1&top=100"))
                },
                "/api/flowRuns/child/causality?skip=1&top=100" => new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FlowRunCausalityPageResponse(origin, [child], 2, null))
                },
                _ => new(HttpStatusCode.NotFound)
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var page = await new FlowApiClient(httpClient).GetFlowRunCausalityAsync("child", default);

        CollectionAssert.AreEqual(new[] { "root", "child" }, page.Value.Select(value => value.FlowRunId).ToArray());
        Assert.AreEqual("analyze", page.Value[1].ParentStepName);
        Assert.AreEqual(2, page.TotalCount);
        Assert.IsNull(page.NextLink);
        CollectionAssert.AreEqual(new[]
        {
            "/api/flowRuns/child/causality?top=100",
            "/api/flowRuns/child/causality?skip=1&top=100"
        }, requests);
    }

    [TestMethod]
    [DataRow("https://example.test/api/flowRuns?skip=1&top=200")]
    [DataRow("//example.test/api/flowRuns?skip=1&top=200")]
    public async Task FlowClientRejectsExternalRunPaginationLinks(string nextLink)
    {
        var requests = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new FlowRunPageResponse([CreateFlowRun("run-1")], nextLink))
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        await Assert.ThrowsExactlyAsync<AgentstrationApiException>(() =>
            new FlowApiClient(httpClient).GetFlowRunsAsync((string?)null, default));

        Assert.AreEqual(1, requests);
    }

}
