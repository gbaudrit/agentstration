using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Configuration;
using Agentstration.Web.Components;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Web.Features.Flows.Designer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        services.AddAgentstrationWebConsole(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerManagementClient>());
        Assert.IsInstanceOfType<RuntimeApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerRuntimeClient>());
        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IManagementApiClient>());
        Assert.IsInstanceOfType<WorkApiClient>(scope.ServiceProvider.GetRequiredService<IWorkApiClient>());
        Assert.IsInstanceOfType<EntryAdministrationApiClient>(scope.ServiceProvider.GetRequiredService<IEntryAdministrationApiClient>());
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
        })) { BaseAddress = new Uri("http://work-api/") };
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
        })) { BaseAddress = new Uri("http://flow-api/") };
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
        var spec = new DirectFlowSpec(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse(flowId.Value, flowId.Value, null, FlowKind.Direct, "1.0.0", true, "1.0.0", spec, new Dictionary<string, string>(), now, now);
        var draft = new FlowDraftResponse(new FlowDraft
        {
            Id = "draft-universal-router", FlowId = flowId, DisplayName = "Universal router",
            Definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] },
            CreatedAt = now, UpdatedAt = now
        }, "\"draft-etag\"");
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/draft", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "flow_draft_not_found", status = 404 }) };
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(draft) };
        })) { BaseAddress = new Uri("http://localhost/") };

        var actual = await new FlowDesignerBackend(new FlowApiClient(httpClient)).GetDraftAsync(flowId.Value, default);

        Assert.AreEqual(flowId, actual.Value.FlowId);
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/flows/universal-router/draft",
            "GET /api/flows/universal-router",
            "POST /api/flows/universal-router/versions/1.0.0/draft"
        }, requests);
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
        })) { BaseAddress = new Uri("http://localhost/") };
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
        var resource = CreateAgentResource("web-agent");
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResponse<AgentResource>([resource], null))
            };
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var agents = await client.GetAgentsAsync(CancellationToken.None);

        Assert.AreEqual("/api/agents", requestPath);
        Assert.HasCount(1, agents);
    }

    [TestMethod]
    public async Task WorkClientListsWorkplaceWorkspacesThroughUnambiguousApiRoute()
    {
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<WorkplaceWorkspaceResponse>()) };
        })) { BaseAddress = new Uri("http://localhost/") };
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
        })) { BaseAddress = new Uri("http://localhost/") };
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
        })) { BaseAddress = new Uri("http://localhost/") };
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
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValueResponse<ModelProviderResponse>([provider])) })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProvidersApiClient(httpClient);

        var providers = await client.GetModelProvidersAsync(default);
        var models = await client.GetProviderModelsAsync("ollama-local", default);

        Assert.AreEqual("aspire", providers[0].Properties.ManagementMode);
        Assert.AreEqual("qwen3:4b", models[0].Name);
        Assert.AreEqual("4B", models[0].Metadata["parameterSize"]);
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
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ModelProfilesApiClient(httpClient);

        _ = await client.UpdateModelProfileAsync(profile.Name, new PutModelProfileRequest(profile.Properties), "\"v1\"", default);
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
    public void ModelProfileEditorPersistsOnlyProviderReferenceModelAndSupportedOptions()
    {
        var editor = new ModelProfileEditorModel
        {
            Name = "reasoning-default", DisplayName = "Default reasoning", ProviderName = "ollama-local",
            ModelName = "qwen3:4b", Temperature = 0.2, MaxOutputTokens = 1000
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
    public void AgentEditorMapsCanonicalReferencesTagsAndTools()
    {
        var model = new AgentEditorModel
        {
            Name = "web-agent",
            DisplayName = "Web Agent",
            Instructions = "Help with web development.",
            ModelProfileName = "reasoning-default",
            ToolNames = "search",
            Tags = "domain=web\nowner=platform"
        };

        var request = model.ToRequest();

        Assert.AreEqual("reasoning-default", request.Definition.ModelProfile.Name);
        Assert.HasCount(1, request.Definition.Tools);
        Assert.AreEqual("web", request.Metadata.Tags["domain"]);
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
            Streaming = StreamingMode.Enabled,
            TimeoutSeconds = 90
        };

        var request = model.ToRequest(agent);

        Assert.AreEqual(agent.Id, request.Agent.ResourceId);
        Assert.AreEqual(7L, request.Agent.Version);
        Assert.AreEqual(RuntimeRunOrigin.Console, request.Origin);
        Assert.AreEqual(90, request.Execution.TimeoutSeconds);
        Assert.AreEqual(StreamingMode.Enabled, request.Execution.Streaming);
        Assert.AreEqual(0.2, request.Execution.Parameters["temperature"].GetDouble());
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
    public async Task AgentRunnerRuntimeClientReadsCanonicalReadinessEndpoint()
    {
        var requested = new List<Uri>();
        var readiness = new AgentRuntimeReadinessResponse("agent-id", 4, true, "Ready", "deployment-id", "revision-id", null);
        var preparation = new PrepareAgentRuntimeResponse("agent-id", 4, "deployment-id", "revision-id", "Ready");
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<object>(request.Method == HttpMethod.Post ? preparation : readiness) };
        })) { BaseAddress = new Uri("http://localhost/") };
        IAgentRunnerRuntimeClient client = new RuntimeApiClient(httpClient);

        var actual = await client.GetAgentReadinessAsync("sql-expert", 4, default);
        var prepared = await client.PrepareAgentAsync("sql-expert", 4, default);

        Assert.IsTrue(actual.Ready);
        Assert.AreEqual("Ready", prepared.State);
        StringAssert.Contains(requested[0].PathAndQuery, "/api/runtime/agents/sql-expert/readiness?generation=4");
        StringAssert.Contains(requested[1].PathAndQuery, "/api/runtime/agents/sql-expert/prepare?generation=4");
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
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Id, 1),
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
            Id = ResourceIdentifier.Create(name).Value,
            Name = name,
            Type = AgentstrationResourceTypes.Agents,
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
        Id = ResourceIdentifier.Create(name).Value,
        Name = name, Type = AgentstrationResourceTypes.ModelProfiles, ApiVersion = ManagementApiVersions.V20260801,
        Properties = new ModelProfileProperties
        {
            DisplayName = "Default reasoning",
            Provider = new ResourceReference(ResourceIdentifier.Create("ollama-local").Value),
            Model = new ModelSelection { Name = "qwen3:4b" }, Generation = new ModelGenerationOptions { Temperature = 0.2 }
        }
    };

    private static ModelProfileSummaryResponse Summary(string name, string provider, string model, string status) => new(
        ResourceIdentifier.Create(name).Value, name,
        new ModelProfileSummaryPropertiesResponse(name, null,
            new ModelProviderReferenceResponse(ResourceIdentifier.Create(provider).Value, provider),
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
        Id = id,
        Name = id,
        Properties = new RuntimeRunProperties
        {
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Id, 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
            Execution = new RuntimeExecutionOptions()
        },
        Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = DateTimeOffset.UtcNow }
    };

    private static RuntimeRunEvent RunEvent(long sequence, RuntimeRunEventKind kind, string? content = null, RuntimeRunState? state = null) => new()
    {
        Sequence = sequence,
        EventId = Guid.NewGuid(),
        RunId = "run-test",
        Kind = kind,
        Timestamp = DateTimeOffset.UtcNow,
        Content = content,
        State = state
    };

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
}
