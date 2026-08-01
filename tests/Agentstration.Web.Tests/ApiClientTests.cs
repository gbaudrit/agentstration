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

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            TimeoutSeconds = 90
        };

        var request = model.ToRequest(agent);

        Assert.AreEqual(agent.Id, request.Agent.ResourceId);
        Assert.AreEqual(7L, request.Agent.Version);
        Assert.AreEqual(RuntimeRunOrigin.Console, request.Origin);
        Assert.AreEqual(90, request.Execution.TimeoutSeconds);
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
