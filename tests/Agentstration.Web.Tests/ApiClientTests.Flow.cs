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
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
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
    public async Task FlowDesignerMaterializesDraftFromActivePublishedVersion()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var flowId = new FlowId("universal-router");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse(flowId.Value, flowId.Value, null, "1.0.0", true, "1.0.0", definition, new Dictionary<string, string>(), now, now);
        var draft = new FlowDraftResponse(new FlowDraft
        {
            WorkspaceId = TestWorkspaceId,
            Id = "draft-universal-router",
            FlowId = flowId,
            DisplayName = "Universal router",
            Definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] },
            CreatedAt = now,
            UpdatedAt = now
        }, "\"draft-etag\"");
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/draft", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "flow_draft_not_found", status = 404 }) };
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/draft/source", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowSourceResponse("entryStep: input", "yaml", 1)) };
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(draft) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var actual = await new FlowDesignerBackend(new FlowApiClient(httpClient)).LoadAsync(new(ResourceNamespace.Default, flowId.Value), default);

        Assert.AreEqual(flowId, actual.Resource.FlowId);
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/flows/universal-router/draft",
            "GET /api/flows/universal-router",
            "POST /api/flows/universal-router/versions/1.0.0/draft",
            "GET /api/flows/universal-router/draft/source"
        }, requests);
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
        })) { BaseAddress = new Uri("http://localhost/") };
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
        })) { BaseAddress = new Uri("http://localhost/") };

        await Assert.ThrowsExactlyAsync<AgentstrationApiException>(() =>
            new FlowApiClient(repeatedHttpClient).GetFlowRunsAsync((string?)null, default));
        Assert.AreEqual(1, repeatedRequests);
    }

    [TestMethod]
    public async Task FlowDesignerLoadsNamespacedPublishedGraphWithoutDraftCallsAndRejectsMutations()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var @namespace = new ResourceNamespace("pack.sample");
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse("sample", "Pack sample", null, "1.2.0", true, "1.2.0", definition, new Dictionary<string, string>(), now, now) { Namespace = @namespace };
        var version = new FlowVersionResponse("sample", "1.2.0", null, definition, new Dictionary<string, string>(), now, graph) { Namespace = @namespace };
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return request.RequestUri.AbsolutePath.EndsWith("/versions/1.2.0", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(version) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var backend = new FlowDesignerBackend(new FlowApiClient(httpClient));
        var target = new FlowDesignerTarget(@namespace, "sample");

        var loaded = await backend.LoadAsync(target, default);

        Assert.AreEqual("1.2.0", loaded.PublishedVersion);
        StringAssert.Contains(loaded.Source, "entryStep: input");
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/namespaces/pack.sample/flows/sample",
            "GET /api/namespaces/pack.sample/flows/sample/versions/1.2.0"
        }, requests);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.SaveDraftAsync(target, new("Sample", null, null, graph), string.Empty, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.ReplaceSourceAsync(target, new("entryStep: input"), string.Empty, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.PublishAsync(target, new("1.3.0"), default));
        using var input = JsonDocument.Parse("{}");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.RunDraftAsync(target, new(input.RootElement.Clone()), default));
        Assert.HasCount(2, requests);
    }

    [TestMethod]
    public async Task FlowDesignerReportsLegacyNamespacedVersionWithoutGraph()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var @namespace = new ResourceNamespace("pack.legacy");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse("legacy", "Legacy", null, "1.0.0", true, "1.0.0", definition, new Dictionary<string, string>(), now, now) { Namespace = @namespace };
        var version = new FlowVersionResponse("legacy", "1.0.0", null, definition, new Dictionary<string, string>(), now) { Namespace = @namespace };
        using var httpClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/versions/1.0.0", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(version) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) }))
        { BaseAddress = new Uri("http://localhost/") };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new FlowDesignerBackend(new FlowApiClient(httpClient)).LoadAsync(new(@namespace, "legacy"), default));

        StringAssert.Contains(exception.Message, "legacy Flow version without a Graph");
    }

}

