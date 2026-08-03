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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void AgentRunnerUsesCanonicalHttpClientsWhenDashboardSimulationIsEnabled()
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
        Assert.IsInstanceOfType<MockApiClient>(scope.ServiceProvider.GetRequiredService<IManagementApiClient>());
    }

    [TestMethod]
    public async Task WorkClientMapsPublicContractToConsoleModel()
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var response = new WorkItemPageResponse(
            [new WorkItemSummaryResponse(Guid.NewGuid(), "review", "Review API", WorkItemStatus.Running, timestamp, timestamp, "operator", "dotnet-expert", 3)],
            null);
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new WorkApiClient(httpClient);

        var items = await client.GetWorkItemsAsync(CancellationToken.None);

        Assert.HasCount(1, items);
        Assert.AreEqual("Review API", items[0].Title);
        Assert.AreEqual("Running", items[0].Status);
        Assert.AreEqual("dotnet-expert", items[0].Owner);
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
    public async Task ManagementClientPreservesETagAndSendsCreatePrecondition()
    {
        var resource = CreateAgentResource("web-agent");
        var sawCreatePrecondition = false;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            sawCreatePrecondition = request.Headers.IfNoneMatch.Any(value => value == EntityTagHeaderValue.Any);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ManagementApiClient(httpClient);

        var snapshot = await client.PutAgentAsync(ToRequest(resource), null, createOnly: true, CancellationToken.None);

        Assert.IsTrue(sawCreatePrecondition);
        Assert.AreEqual("\"v1\"", snapshot.ETag);
        Assert.AreEqual("web-agent", snapshot.Value.Name);
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
        await client.DeleteAgentAsync("default", "web-agent", "\"v2\"", CancellationToken.None);

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

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetAgentAsync("default", "web-agent", CancellationToken.None));

        Assert.IsTrue(exception.IsConcurrencyConflict);
        Assert.AreEqual("precondition_failed", exception.ProblemTitle);
        Assert.AreEqual("The ETag is stale.", exception.Message);
    }

    [TestMethod]
    public async Task ModelProvidersClientMapsProviderAndDynamicModels()
    {
        var provider = new ModelProviderResponse("provider-id", "ollama-local", "default", "local", new ModelProviderPropertiesResponse("Ollama local", "ollama", "aspire", "available", "ollama", 1));
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

        _ = await client.UpdateModelProfileAsync("default", profile.Name, new PutModelProfileRequest(profile.Properties), "\"v1\"", default);
        await client.DeleteModelProfileAsync("default", profile.Name, "\"v2\"", default);

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
            Name = "reasoning-default", DisplayName = "Default reasoning", ProviderResourceId = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.ModelProviders, "modelProviders", "ollama-local").Value,
            ModelName = "qwen3:4b", Temperature = 0.2, MaxOutputTokens = 1000
        };

        var request = editor.ToCreateRequest();

        Assert.AreEqual("ollama-local", ResourceIdentifier.Parse(request.Properties.Provider.ResourceId).Name);
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

        var actual = await new AgentsModelApiClient(httpClient).GetAgentModelResolutionAsync("default", "sql-expert", default);

        Assert.AreEqual("qwen3:4b", actual.Resolved.Model.Name);
        StringAssert.Contains(requested!.PathAndQuery, "/api/agents/sql-expert/model?resourceGroup=default");
    }

    [TestMethod]
    public void AgentEditorMapsCanonicalReferencesTagsAndTools()
    {
        var model = new AgentEditorModel
        {
            Name = "web-agent",
            ResourceGroup = "default",
            Location = "local",
            DisplayName = "Web Agent",
            AgentTypeResourceId = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value,
            AgentTypeVersion = 2,
            ModelProfileResourceId = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "reasoning-default").Value,
            ToolResourceIds = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Tools, "tools", "search").Value,
            Tags = "domain=web\nowner=platform"
        };

        var request = model.ToRequest();

        Assert.AreEqual(2, request.Properties.AgentType.Version);
        Assert.HasCount(1, request.Properties.Tools);
        Assert.AreEqual("web", request.Tags!["domain"]);
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

        var actual = await client.GetAgentReadinessAsync("default", "sql-expert", 4, default);
        var prepared = await client.PrepareAgentAsync("default", "sql-expert", 4, default);

        Assert.IsTrue(actual.Ready);
        Assert.AreEqual("Ready", prepared.State);
        StringAssert.Contains(requested[0].PathAndQuery, "/api/runtime/agents/sql-expert/readiness?resourceGroup=default&generation=4");
        StringAssert.Contains(requested[1].PathAndQuery, "/api/runtime/agents/sql-expert/prepare?resourceGroup=default&generation=4");
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
            Id = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agents", name).Value,
            Name = name,
            Type = AgentstrationResourceTypes.Agents,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = "default",
            Location = "local",
            Generation = 1,
            ETag = etag,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted, ResourceVersion = etag },
            Properties = new AgentProperties
            {
                DisplayName = name,
                AgentType = new AgentTypeReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value, 1),
                ModelProfile = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "reasoning-default").Value)
            }
        };
    }

    private static ModelProfileResource CreateModelProfile(string name) => new()
    {
        Id = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", name).Value,
        Name = name, Type = AgentstrationResourceTypes.ModelProfiles, ApiVersion = ManagementApiVersions.V20260801, ResourceGroup = "default", Location = "local",
        Properties = new ModelProfileProperties
        {
            DisplayName = "Default reasoning",
            Provider = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.ModelProviders, "modelProviders", "ollama-local").Value),
            Model = new ModelSelection { Name = "qwen3:4b" }, Generation = new ModelGenerationOptions { Temperature = 0.2 }
        }
    };

    private static ModelProfileSummaryResponse Summary(string name, string provider, string model, string status) => new(
        ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", name).Value, name, "default", "local",
        new ModelProfileSummaryPropertiesResponse(name, null,
            new ModelProviderReferenceResponse(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.ModelProviders, "modelProviders", provider).Value, provider),
            new ModelReferenceResponse(model), new ModelGenerationOptions(), new ModelReasoningOptions(), new ModelOutputOptions(), status, 0));

    private static AgentResourceRequest ToRequest(AgentResource resource) => new()
    {
        Type = resource.Type,
        ApiVersion = resource.ApiVersion,
        Name = resource.Name,
        ResourceGroup = resource.ResourceGroup!,
        Location = resource.Location!,
        Tags = resource.Tags,
        Properties = resource.Properties
    };

    private static RuntimeRun CreateRun(string id) => new()
    {
        Id = id,
        Name = id,
        ResourceGroup = "default",
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
}
