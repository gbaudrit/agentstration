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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ApiClientTests
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void OidcApisPreferBearerButAcceptTheTrustedConsoleSession()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Authentication:Mode"] = Agentstration.Web.Configuration.AuthenticationOptions.Oidc,
            ["Agentstration:Authentication:Authority"] = "https://identity.example/",
            ["Agentstration:Authentication:Audience"] = "agentstration-api",
            ["Agentstration:Authentication:ClientId"] = "agentstration-console"
        }).Build();
        services.AddLogging();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>()
            .Get(AgentstrationAuthenticationDefaults.PolicyScheme).ForwardDefaultSelector;
        Assert.IsNotNull(selector);

        var api = new DefaultHttpContext();
        api.Request.Path = "/api/agents";
        Assert.AreEqual(JwtBearerDefaults.AuthenticationScheme, selector(api));

        api.Request.Headers.Cookie = $"{AgentstrationAuthenticationDefaults.ApplicationCookie}=session";
        Assert.AreEqual(IdentityConstants.ApplicationScheme, selector(api));

        api.Request.Headers.Authorization = "Bearer access-token";
        Assert.AreEqual(JwtBearerDefaults.AuthenticationScheme, selector(api));
    }

    [TestMethod]
    public void AgentManagementAndRunnerUseCanonicalHttpClientsWhenDashboardSimulationIsEnabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:UseSimulatedData"] = "true",
            ["Agentstration:ManagementApi:BaseAddress"] = "http://localhost:5080/",
            ["Agentstration:RuntimeApi:BaseAddress"] = "http://localhost:5080/"
        }).Build();
        services.AddLogging();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerManagementClient>());
        Assert.IsInstanceOfType<RuntimeApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerRuntimeClient>());
        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IManagementApiClient>());
        Assert.IsInstanceOfType<WorkApiClient>(scope.ServiceProvider.GetRequiredService<IWorkApiClient>());
        Assert.IsInstanceOfType<EntryAdministrationApiClient>(scope.ServiceProvider.GetRequiredService<IEntryAdministrationApiClient>());
        Assert.IsInstanceOfType<PacksApiClient>(scope.ServiceProvider.GetRequiredService<IPacksClient>());
        Assert.IsInstanceOfType<ConsoleResourceSearchProvider>(scope.ServiceProvider.GetRequiredService<IResourceSearchProvider>());
    }

    [TestMethod]
    public async Task WorkClientMapsPublicContractToConsoleModel()
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var response = new WorkTaskOperationsPageResponse(
            [new WorkTaskOperationsSummary(Guid.NewGuid(), "personal", "review", Guid.NewGuid(), "Review API", null, WorkTaskStatus.Running, timestamp, timestamp, timestamp, null, "flowrun-1", null, 0, 0, 0, 1, "Work started", null)],
            1, 100, 1);
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new WorkApiClient(httpClient);

        var items = await client.GetWorkItemsAsync(CancellationToken.None);

        Assert.HasCount(1, items);
        Assert.AreEqual("Review API", items[0].Title);
        Assert.AreEqual("Running", items[0].Status);
        Assert.AreEqual("personal", items[0].Owner);
    }

    [TestMethod]
    public async Task WorkClientExposesSafeErrorIdentifier()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new WorkApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetWorkItemsAsync(CancellationToken.None));

        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.ErrorId));
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

    [TestMethod]
    public async Task ManagementClientPreservesETagAndSendsCreatePrecondition()
    {
        var resource = CreateAgentResource("web-agent");
        var sawCreatePrecondition = false;
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            sawCreatePrecondition = request.Headers.IfNoneMatch.Any(value => value == EntityTagHeaderValue.Any);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var snapshot = await client.PutAgentAsync(ToRequest(resource), null, createOnly: true, CancellationToken.None);

        Assert.IsTrue(sawCreatePrecondition);
        Assert.AreEqual("/api/agents/web-agent", requestPath);
        Assert.AreEqual("\"v1\"", snapshot.ETag);
        Assert.AreEqual("web-agent", snapshot.Value.Name);
    }

    [TestMethod]
    public async Task ManagementClientListsAgentsThroughApiInsteadOfRazorPage()
    {
        var @namespace = new ResourceNamespace("agentstration.who-am-i");
        var resource = CreateAgentResource("web-agent") with
        {
            Metadata = CreateAgentResource("web-agent").Metadata with { Namespace = @namespace },
            Definition = CreateAgentResource("web-agent").Definition with
            {
                ModelProfile = new ResourceReference("reasoning-shared", @namespace: new ResourceNamespace("shared.models"))
            }
        };
        string? requestPathAndQuery = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPathAndQuery = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResponse<AgentResource>([resource], null))
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var agents = await client.GetAgentsAsync(CancellationToken.None);

        Assert.AreEqual("/api/agents?allNamespaces=true&top=1000", requestPathAndQuery);
        Assert.HasCount(1, agents);
        Assert.AreEqual(@namespace, agents[0].Namespace);
        Assert.AreEqual(new ResourceNamespace("shared.models"), agents[0].ModelProfileNamespace);
        Assert.AreEqual("/modelprofiles/reasoning-shared?namespace=shared.models", ConsoleResourceUrls.ModelProfile(agents[0].ModelProfileAddress));
        Assert.AreEqual("/namespaces/agentstration.who-am-i/agents/web-agent", agents[0].DetailsUrl);
    }

    [TestMethod]
    public async Task ManagementClientGetsAgentFromItsNamespace()
    {
        var @namespace = new ResourceNamespace("agentstration.who-am-i");
        var resource = CreateAgentResource("who-am-i-judge") with
        {
            Metadata = CreateAgentResource("who-am-i-judge").Metadata with { Namespace = @namespace }
        };
        Uri? requested = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"stored\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var actual = await client.GetAgentAsync(@namespace, resource.Metadata.Name, CancellationToken.None);

        Assert.AreEqual("/api/namespaces/agentstration.who-am-i/agents/who-am-i-judge", requested!.AbsolutePath);
        Assert.AreEqual(@namespace, actual.Value.Namespace);
    }

    [TestMethod]
    public async Task WorkClientListsWorkplaceWorkspacesThroughUnambiguousApiRoute()
    {
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<WorkplaceWorkspaceResponse>()) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new WorkApiClient(httpClient);

        _ = await client.GetWorkspacesAsync(CancellationToken.None);

        Assert.AreEqual("/api/workplace/workspaces", requestPath);
    }

    [TestMethod]
    public async Task ManagementClientSendsETagForUpdateAndDelete()
    {
        var resource = CreateAgentResource("web-agent");
        var methods = new List<(HttpMethod Method, string? IfMatch)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            methods.Add((request.Method, request.Headers.IfMatch.FirstOrDefault()?.ToString()));
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method != HttpMethod.Delete)
            {
                response.Content = JsonContent.Create(resource);
                response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            }
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        _ = await client.PutAgentAsync(ToRequest(resource), "\"v1\"", createOnly: false, CancellationToken.None);
        await client.DeleteAgentAsync("web-agent", "\"v2\"", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { (HttpMethod.Put, "\"v1\""), (HttpMethod.Delete, "\"v2\"") }, methods);
    }

    [TestMethod]
    public async Task ManagementClientExposesProblemDetailsAndConcurrencyConflict()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = JsonContent.Create(new { title = "precondition_failed", detail = "The ETag is stale.", status = 412 })
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetAgentAsync("web-agent", CancellationToken.None));

        Assert.IsTrue(exception.IsConcurrencyConflict);
        Assert.AreEqual("precondition_failed", exception.ProblemTitle);
        Assert.AreEqual("The ETag is stale.", exception.Message);
    }

    [TestMethod]
    public async Task ModelProvidersClientMapsProviderAndDynamicModels()
    {
        var provider = new ModelProviderResponse("provider-id", "ollama-local", new ModelProviderPropertiesResponse("Ollama local", "ollama", "aspire", "available", "ollama", 1));
        var model = new AvailableModelResponse("qwen3:4b", "Qwen 3 4B", "available", ["chat"], new Dictionary<string, string> { ["parameterSize"] = "4B" });
        using var httpClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<AvailableModelResponse>([model])) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<ModelProviderResponse>([provider])) }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProvidersApiClient(httpClient);

        var providers = await client.GetModelProvidersAsync(default);
        var models = await client.GetProviderModelsAsync("ollama-local", default);

        Assert.AreEqual("aspire", providers[0].Properties.ManagementMode);
        Assert.AreEqual("qwen3:4b", models[0].Name);
        Assert.AreEqual("4B", models[0].Metadata["parameterSize"]);
    }

    [TestMethod]
    public async Task ModelManagementClientsPreserveNamespaceInResourceRequests()
    {
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            object response = request.RequestUri.AbsolutePath switch
            {
                var path when path.Contains("modelproviders", StringComparison.Ordinal) => new ValueResponse<AvailableModelResponse>([]),
                var path when path.Contains("modelprofiles", StringComparison.Ordinal) => new ModelProfileUsagesResponse([], 0),
                _ => new RuntimeProfileUsagesResponse([], 0)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var resourceNamespace = new ResourceNamespace("team-a");

        _ = await new ModelProvidersApiClient(httpClient).GetProviderModelsAsync(resourceNamespace, "shared", default);
        _ = await new ModelProfilesApiClient(httpClient).GetModelProfileUsagesAsync(resourceNamespace, "shared", default);
        _ = await new RuntimeProfilesApiClient(httpClient).GetRuntimeProfileUsagesAsync(resourceNamespace, "shared", default);

        CollectionAssert.AreEqual(new[]
        {
            "/api/modelproviders/shared/models?resourceNamespace=team-a",
            "/api/modelprofiles/shared/usages?resourceNamespace=team-a",
            "/api/runtimeprofiles/shared/usages?resourceNamespace=team-a"
        }, requests);
    }

    [TestMethod]
    public async Task ModelProfilesClientPreservesETagForUpdateAndDelete()
    {
        var profile = CreateModelProfile("reasoning-default");
        var requests = new List<(HttpMethod Method, string? IfMatch)>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.Headers.IfMatch.FirstOrDefault()?.ToString()));
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method != HttpMethod.Delete) { response.Content = JsonContent.Create(profile); response.Headers.ETag = new EntityTagHeaderValue("\"v2\""); }
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProfilesApiClient(httpClient);

        _ = await client.UpdateModelProfileAsync(profile.Name, new PutModelProfileRequest(profile.Definition), "\"v1\"", default);
        await client.DeleteModelProfileAsync(profile.Name, "\"v2\"", default);

        CollectionAssert.AreEqual(new[] { (HttpMethod.Put, "\"v1\""), (HttpMethod.Delete, "\"v2\"") }, requests);
    }

    [TestMethod]
    public void ModelProfilePickerFilteringCoversNameProviderAndModelAndRejectsInvalidProfiles()
    {
        var ready = Summary("reasoning-default", "ollama-local", "qwen3:4b", "ready");
        var invalid = Summary("broken", "remote-provider", "missing", "invalidConfiguration");

        Assert.HasCount(1, ModelManagementUi.FilterProfiles([ready, invalid], "qwen3"));
        Assert.HasCount(1, ModelManagementUi.FilterProfiles([ready, invalid], "remote-provider"));
        Assert.IsFalse(ModelManagementUi.IsInvalid(ready));
        Assert.IsTrue(ModelManagementUi.IsInvalid(invalid));
    }

    [TestMethod]
    public async Task SecretsClientWritesValueThroughDedicatedEndpointAndNeverOffersReadValue()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new SecretsApiClient(httpClient);

        await client.SetSecretValueAsync("openai-key", "sensitive-value", default);

        Assert.AreEqual(HttpMethod.Put, method);
        Assert.AreEqual("/api/secrets/openai-key/value", path);
        StringAssert.Contains(body, "sensitive-value");
        Assert.IsFalse(typeof(ISecretsClient).GetMethods().Any(value => value.Name.Contains("GetSecretValue", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task VaultInitializationPostsToDedicatedEndpointWithoutReturningKeyMaterial()
    {
        HttpMethod? method = null;
        string? path = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new VaultInitializationResponse("initialized", "C:\\data\\secrets\\master.key"))
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new SecretsApiClient(httpClient);

        var response = await client.InitializeVaultAsync("local vault", default);

        Assert.AreEqual(HttpMethod.Post, method);
        Assert.AreEqual("/api/vaults/local%20vault/initialize", path);
        Assert.AreEqual("initialized", response.Status);
        Assert.AreEqual("C:\\data\\secrets\\master.key", response.KeyFilePath);
        CollectionAssert.AreEquivalent(
            new[] { nameof(VaultInitializationResponse.Status), nameof(VaultInitializationResponse.KeyFilePath) },
            typeof(VaultInitializationResponse).GetProperties().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void ModelProviderEditorPersistsOnlySecretReference()
    {
        var editor = new ModelProviderEditorModel
        {
            Name = "openai",
            DisplayName = "OpenAI",
            ProviderType = "openai",
            Endpoint = "https://extension.example.test",
            CredentialId = "default:openai-api-key"
        };

        var properties = editor.ToProperties();

        Assert.IsNotNull(properties.Credential);
        Assert.AreEqual("openai-api-key", properties.Credential.Name);
        Assert.IsNull(properties.Credential.Namespace);
    }

    [TestMethod]
    public void SecretEditorBuildsAValidIdentifierFromDisplayName()
    {
        Assert.AreEqual("cle-openai-production", SecretEditorModel.IdentifierFromDisplayName("  Clé OpenAI — Production  "));
        Assert.AreEqual("github-token", SecretEditorModel.IdentifierFromDisplayName("GitHub___Token"));
        Assert.AreEqual(string.Empty, SecretEditorModel.IdentifierFromDisplayName("---"));
    }

    [TestMethod]
    public void ModelProfileEditorPersistsOnlyProviderReferenceModelAndSupportedOptions()
    {
        var editor = new ModelProfileEditorModel
        {
            Name = "reasoning-default",
            DisplayName = "Default reasoning",
            ProviderName = "ollama-local",
            ModelName = "qwen3:4b",
            Temperature = 0.2,
            MaxOutputTokens = 1000
        };

        var request = editor.ToCreateRequest();

        Assert.AreEqual("ollama-local", request.Properties.Provider.Name);
        Assert.AreEqual("qwen3:4b", request.Properties.Model.Name);
        Assert.AreEqual(0.2, request.Properties.Generation.Temperature);
    }

    [TestMethod]
    public async Task AgentsModelClientUsesAgentResolutionEndpoint()
    {
        Uri? requested = null;
        var response = new AgentModelResponse(
            new DeclaredAgentModelResponse(new ModelProfileIdentityResponse("profile-id", "reasoning-default", "Default reasoning")),
            new ResolvedAgentModelResponse(new ModelProviderReferenceResponse("provider-id", "ollama-local", "Ollama local", "ollama", "available"), new ModelReferenceResponse("qwen3:4b", "available"), new EffectiveModelOptionsResponse(new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 }, new ModelReasoningOptions(), new ModelOutputOptions())),
            "ready", []);
        using var httpClient = new HttpClient(new StubHandler(request => { requested = request.RequestUri; return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }; })) { BaseAddress = new Uri("http://localhost/") };

        var actual = await new AgentsModelApiClient(httpClient).GetAgentModelResolutionAsync("sql-expert", default);

        Assert.AreEqual("qwen3:4b", actual.Resolved.Model.Name);
        Assert.AreEqual("/api/agents/sql-expert/model", requested!.PathAndQuery);
    }

    [TestMethod]
    public async Task AgentsModelClientKeepsTheAgentNamespace()
    {
        Uri? requested = null;
        var response = new AgentModelResponse(
            new DeclaredAgentModelResponse(new ModelProfileIdentityResponse("reasoning", "reasoning", Namespace: "shared.models")),
            new ResolvedAgentModelResponse(null, new ModelReferenceResponse("qwen3:4b"), new EffectiveModelOptionsResponse(new(), new(), new())),
            "ready", []);
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var actual = await new AgentsModelApiClient(httpClient).GetAgentModelResolutionAsync(new ResourceNamespace("agentstration.daily-life-assistant"), "concierge", default);

        Assert.AreEqual("shared.models", actual.Declared.ModelProfile.Namespace);
        Assert.AreEqual("/api/namespaces/agentstration.daily-life-assistant/agents/concierge/model", requested!.AbsolutePath);
    }

    [TestMethod]
    public void AgentEditorMapsCanonicalReferencesTagsAndTools()
    {
        var model = new AgentEditorModel
        {
            Name = "web-agent",
            DisplayName = "Web Agent",
            Instructions = "Help with web development.",
            ModelProfileName = "reasoning-default",
            ModelProfileNamespace = "shared.models",
            RuntimeProfileName = "maf-shared",
            RuntimeProfileNamespace = "shared.platform",
            ToolNames = "search",
            Tags = "domain=web\nowner=platform"
        };

        var request = model.ToRequest();

        Assert.AreEqual("reasoning-default", request.Definition.ModelProfile.Name);
        Assert.AreEqual(new ResourceNamespace("shared.models"), request.Definition.ModelProfile.Namespace);
        Assert.AreEqual("maf-shared", request.Definition.RuntimeProfile.Name);
        Assert.AreEqual(new ResourceNamespace("shared.platform"), request.Definition.RuntimeProfile.Namespace);
        Assert.HasCount(1, request.Definition.Tools);
        Assert.AreEqual("web", request.Metadata.Tags["domain"]);
    }

    [TestMethod]
    public void AgentEditorMarksOnlyAnEffectiveModelProfileChange()
    {
        var model = new AgentEditorModel
        {
            ModelProfileName = "default-reasoning",
            ModelProfileNamespace = "default"
        };
        var current = Summary("default-reasoning", "ollama-local", "qwen3:1.7b", "available");
        var namespaced = current with { Namespace = "shared.models" };

        Assert.IsFalse(model.SelectModelProfile(current));
        Assert.IsTrue(model.SelectModelProfile(namespaced));
        Assert.AreEqual("shared.models", model.ModelProfileNamespace);
    }

    [TestMethod]
    public void AgentRunnerBuildsVersionedRuntimePayloadAndValidatesJson()
    {
        var agent = CreateAgentResource("web-agent") with { Generation = 7 };
        var model = new AgentRunnerModel
        {
            Prompt = "Optimize this query",
            Context = "{\"engine\":\"sqlserver\"}",
            RuntimeParameters = "{\"temperature\":0.2}",
            Streaming = RuntimeStreamingMode.Enabled,
            ToolArgumentRetention = ToolArgumentRetentionMode.Retain,
            TimeoutSeconds = 90
        };

        var request = model.ToRequest(agent);

        Assert.AreEqual(agent.Metadata.Name, request.Agent.ResourceId);
        Assert.AreEqual(7L, request.Agent.Version);
        Assert.AreEqual(RuntimeRunOrigin.Console, request.Origin);
        Assert.AreEqual(90, request.Execution.TimeoutSeconds);
        Assert.AreEqual(RuntimeStreamingMode.Enabled, request.Execution.Streaming);
        Assert.AreEqual(true, request.Execution.PersistToolArguments);
        Assert.AreEqual(0.2, request.Execution.Parameters["temperature"].GetDouble());
    }

    [TestMethod]
    public void AgentRunnerMapsAllToolArgumentRetentionModes()
    {
        var agent = CreateAgentResource("web-agent");

        bool? Map(ToolArgumentRetentionMode mode) => new AgentRunnerModel
        {
            Prompt = "test",
            ToolArgumentRetention = mode
        }.ToRequest(agent).Execution.PersistToolArguments;

        Assert.IsNull(Map(ToolArgumentRetentionMode.Inherit));
        Assert.AreEqual(true, Map(ToolArgumentRetentionMode.Retain));
        Assert.AreEqual(false, Map(ToolArgumentRetentionMode.DoNotRetain));
    }

    [TestMethod]
    public async Task RuntimeClientProcessesProgressiveSseAndClosesAtEndOfStream()
    {
        var first = RunEvent(1, RuntimeRunEventKind.StatusChanged, state: RuntimeRunState.Running);
        var second = RunEvent(2, RuntimeRunEventKind.ResponseDelta, content: "partial response");
        var third = RunEvent(3, RuntimeRunEventKind.RunCompleted, state: RuntimeRunState.Succeeded);
        var payload = string.Join(string.Empty, new[] { first, second, third }.Select(item => $"id: {item.Sequence}\nevent: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonOptions)}\n\n"));
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })) { BaseAddress = new Uri("http://localhost/") };
        var client = new RuntimeApiClient(httpClient);
        var state = new AgentRunnerState();
        state.Reset(CreateRun("run-test"));

        await foreach (var runEvent in client.ObserveRunAsync("run-test", 0, default)) state.Apply(runEvent);

        Assert.AreEqual("partial response", state.Response);
        Assert.AreEqual(RuntimeRunState.Succeeded, state.State);
        Assert.HasCount(3, state.Events);
    }

    [TestMethod]
    public void AgentRunnerRestoresToolCallsFromTheDurableRunProjection()
    {
        var persistedToolCall = new RuntimeToolCall
        {
            Id = "tool-call-1",
            InvocationId = "invocation-1",
            ToolId = "microsoft-learn.microsoft_docs_search",
            Name = "microsoft_docs_search",
            State = RuntimeRunState.Succeeded,
            Attempt = 1,
            StartedAt = DateTimeOffset.UtcNow
        };
        var originalRun = CreateRun("run-with-tool");
        var run = originalRun with
        {
            Status = originalRun.Status with { ToolCalls = [persistedToolCall] }
        };
        var state = new AgentRunnerState();

        state.Reset(run);

        Assert.HasCount(1, state.ToolCalls);
        Assert.AreEqual(persistedToolCall, state.ToolCalls[0]);
    }

    [TestMethod]
    public async Task AgentRunnerRuntimeClientReadsCanonicalReadinessEndpoint()
    {
        var requested = new List<Uri>();
        var readiness = new AgentRuntimeReadinessResponse("agent-id", 4, true, "Ready", "deployment-id", "revision-id", null);
        var preparation = new PrepareAgentRuntimeResponse("agent-id", 4, "deployment-id", "revision-id", "Ready");
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<object>(request.Method == HttpMethod.Post ? preparation : readiness) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        IAgentRunnerRuntimeClient client = new RuntimeApiClient(httpClient);

        var actual = await client.GetAgentReadinessAsync("sql-expert", 4, default);
        var prepared = await client.PrepareAgentAsync("sql-expert", 4, default);
        var agentNamespace = new ResourceNamespace("agentstration.daily-life-assistant");
        var namespaced = await client.GetAgentReadinessAsync(agentNamespace, "concierge", 5, default);
        var namespacedPreparation = await client.PrepareAgentAsync(agentNamespace, "concierge", 5, default);

        Assert.IsTrue(actual.Ready);
        Assert.AreEqual("Ready", prepared.State);
        Assert.IsTrue(namespaced.Ready);
        Assert.AreEqual("Ready", namespacedPreparation.State);
        StringAssert.Contains(requested[0].PathAndQuery, "/api/runtime/agents/sql-expert/readiness?generation=4");
        StringAssert.Contains(requested[1].PathAndQuery, "/api/runtime/agents/sql-expert/prepare?generation=4");
        StringAssert.Contains(requested[2].PathAndQuery, "/api/runtime/namespaces/agentstration.daily-life-assistant/agents/concierge/readiness?generation=5");
        StringAssert.Contains(requested[3].PathAndQuery, "/api/runtime/namespaces/agentstration.daily-life-assistant/agents/concierge/prepare?generation=5");
    }

    [TestMethod]
    public void AgentRunnerRejectsProviderAndModelOverrides()
    {
        var model = new AgentRunnerModel { Prompt = "test", RuntimeParameters = "{\"model\":\"other\"}" };

        var exception = Assert.ThrowsExactly<ArgumentException>(() => model.ToRequest(CreateAgentResource("web-agent")));

        StringAssert.Contains(exception.Message, "not supported");
    }

    [TestMethod]
    public async Task SimulatedRuntimeRetryCreatesNewRunIdentifier()
    {
        var client = new MockApiClient(TimeProvider.System);
        var request = new CreateRuntimeRunRequest
        {
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Metadata.Name, 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] }
        };
        var original = await client.CreateRunAsync(request, default);

        var retry = await client.RetryRunAsync(original.Id, default);

        Assert.AreNotEqual(original.Id, retry.Id);
        Assert.AreEqual(original.Properties.Input, retry.Properties.Input);
    }

    private static AgentResource CreateAgentResource(string name)
    {
        var etag = "\"stored\"";
        return new AgentResource
        {
            Metadata = new ResourceMetadata { Name = name },
            Kind = ResourceKinds.Agent,
            ApiVersion = ManagementApiVersions.V20260801,
            Generation = 1,
            ETag = etag,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted, ResourceVersion = etag },
            Definition = new AgentProperties
            {
                DisplayName = name,
                Instructions = "Help the user.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        };
    }

    private static ModelProfileResource CreateModelProfile(string name) => new()
    {
        Metadata = new ResourceMetadata { Name = name },
        Kind = ResourceKinds.ModelProfile,
        ApiVersion = ManagementApiVersions.V20260801,
        Definition = new ModelProfileProperties
        {
            DisplayName = "Default reasoning",
            Provider = new ResourceReference("ollama-local"),
            Model = new ModelSelection { Name = "qwen3:4b" },
            Generation = new ModelGenerationOptions { Temperature = 0.2 }
        }
    };

    private static ModelProfileSummaryResponse Summary(string name, string provider, string model, string status) => new(
        name, name,
        new ModelProfileSummaryPropertiesResponse(name, null,
            new ModelProviderReferenceResponse(provider, provider),
            new ModelReferenceResponse(model), new ModelGenerationOptions(), new ModelReasoningOptions(), new ModelOutputOptions(), status, 0));

    private static AgentResourceRequest ToRequest(AgentResource resource) => new()
    {
        ApiVersion = resource.ApiVersion,
        Kind = resource.Kind,
        Metadata = resource.Metadata,
        Definition = resource.Definition
    };

    private static RuntimeRun CreateRun(string id) => new()
    {
        WorkspaceId = TestWorkspaceId,
        Scope = new RuntimeRunScope(Guid.Empty, TestWorkspaceId, Guid.Empty),
        Id = id,
        Name = id,
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Metadata.Name, 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions()
        },
        Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = DateTimeOffset.UtcNow }
    };

    private static RuntimeRunEvent RunEvent(long sequence, RuntimeRunEventKind kind, string? content = null, RuntimeRunState? state = null) => new()
    {
        WorkspaceId = TestWorkspaceId,
        Sequence = sequence,
        EventId = Guid.NewGuid(),
        RunId = "run-test",
        Kind = kind,
        Timestamp = DateTimeOffset.UtcNow,
        Content = content,
        State = state
    };

    private static readonly Agentstration.Resources.WorkspaceId TestWorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = nameof(ApiClientTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
