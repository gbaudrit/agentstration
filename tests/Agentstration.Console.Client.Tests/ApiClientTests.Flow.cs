using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
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
            Assert.AreEqual(ResourceKinds.Flow, Uri.UnescapeDataString(request.RequestUri.Query.Replace("?kind=", "", StringComparison.Ordinal)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new ResourcePickerItem("universal-router", "Universal router", null, "1.0.0", "Active", ResourceKinds.Flow),
                    new ResourcePickerItem("my-flow", "My flow", null, "1.0.0", "Active", ResourceKinds.Flow)
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
