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
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void DirectFlowRequiresExactlyOneTypedTarget()
    {
        var valid = Definition("direct", FlowKind.Direct, new DirectFlowSpec(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        FlowValidator.Validate(valid);
        var exception = Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("invalid", FlowKind.Direct, new DirectFlowSpec(null!))));
        Assert.AreEqual("flow_target_required", exception.Code);
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("direct-flow", FlowKind.Direct,
            new DirectFlowSpec(new FlowTargetReference(FlowTargetKind.Flow, "child")))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("mismatch", FlowKind.Routing, valid.Spec)));
    }

    [TestMethod]
    public void RoutingWorkflowOrchestrationAndCompositeValidateStructure()
    {
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("routing", FlowKind.Routing,
            new RoutingFlowSpec(FlowRoutingStrategy.Capabilities, []))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("workflow-entry", FlowKind.Workflow,
            new WorkflowFlowSpec("missing", [new FlowNode("start", FlowNodeKind.Function)], []))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("workflow-edge", FlowKind.Workflow,
            new WorkflowFlowSpec("start", [new FlowNode("start", FlowNodeKind.Function)], [new FlowEdge("start", "missing")]))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("orchestration", FlowKind.Orchestration,
            new OrchestrationFlowSpec(FlowOrchestrationStrategy.Sequential, []))));
        Assert.Throws<FlowValidationException>(() => FlowValidator.Validate(Definition("self", FlowKind.Composite,
            new CompositeFlowSpec(FlowCompositionMode.Sequential, [new FlowReference(new FlowId("self"), "1.0.0", false)]))));
    }

    [TestMethod]
    public void EveryFlowSpecRoundTripsWithDiscriminator()
    {
        FlowSpec[] specs =
        [
            new DirectFlowSpec(new FlowTargetReference(FlowTargetKind.Agent, "agent-a")),
            new RoutingFlowSpec(FlowRoutingStrategy.Capabilities, [new FlowTargetReference(FlowTargetKind.AgentType, "expert")]),
            new WorkflowFlowSpec("start", [new FlowNode("start", FlowNodeKind.Function)], []),
            new OrchestrationFlowSpec(FlowOrchestrationStrategy.Concurrent, [new FlowTargetReference(FlowTargetKind.Agent, "agent-a")], 3),
            new CompositeFlowSpec(FlowCompositionMode.Sequential, [new FlowReference(new FlowId("child"), "1.0.0", false)])
        ];

        foreach (var spec in specs)
        {
            var json = JsonSerializer.Serialize(spec, JsonOptions);
            StringAssert.Contains(json, "specKind");
            var restored = JsonSerializer.Deserialize<FlowSpec>(json, JsonOptions);
            Assert.AreEqual(spec.GetType(), restored!.GetType());
        }
    }

    [TestMethod]
    public async Task FlowServicePersistsVersionsResolvesActiveAndEnforcesConcurrency()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(new CreateFlowCommand("technical-router", "Routes work", FlowKind.Routing, "1.0.0", true,
            new RoutingFlowSpec(FlowRoutingStrategy.Capabilities, [new FlowTargetReference(FlowTargetKind.AgentType, "technical-expert")])), default);
        var published = await fixture.Service.PublishVersionAsync(created.Value.Id, "1.0.0", true, default);
        var precise = await fixture.Service.GetVersionAsync(created.Value.Id, "1.0.0", default);
        var resolved = await fixture.Service.ResolveAsync(new FlowReference(created.Value.Id), default);

        Assert.AreEqual(JsonSerializer.Serialize(published.Value, JsonOptions), JsonSerializer.Serialize(precise!.Value, JsonOptions));
        Assert.AreEqual("1.0.0", resolved.Version);
        Assert.AreEqual("1.0.0", (await fixture.Service.GetAsync(created.Value.Id, default))!.Value.ActiveVersion);
        await Assert.ThrowsAsync<FlowConcurrencyException>(() => fixture.Service.UpdateAsync(created.Value.Id,
            new UpdateFlowCommand("Changed", FlowKind.Routing, "1.1.0", true, created.Value.Spec), "\"stale\"", default));
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
        var request = new CreateFlowRequest("direct-sql", "Direct SQL work", FlowKind.Direct, "1.0.0", true,
            new DirectFlowSpec(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        using var createdResponse = await client.PostAsJsonAsync("/api/flows", request, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.IsInstanceOfType<DirectFlowSpec>(created!.Spec);

        using var versionResponse = await client.PostAsJsonAsync("/api/flows/direct-sql/versions", new CreateFlowVersionRequest("1.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, versionResponse.StatusCode);
        var version = await client.GetFromJsonAsync<FlowVersionResponse>("/api/flows/direct-sql/versions/1.0.0", JsonOptions);
        Assert.AreEqual("1.0.0", version!.Version);

        var get = await client.GetAsync("/api/flows/direct-sql");
        var etag = get.Headers.ETag!.Tag;
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/flows/direct-sql")
        {
            Content = JsonContent.Create(new UpdateFlowRequest("Updated", FlowKind.Direct, "1.1.0", true, request.Spec), options: JsonOptions)
        };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var list = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows");
        Assert.AreEqual(1, list!.Value.Count);

        var openApi = await client.GetStringAsync("/openapi/v1.json");
        StringAssert.Contains(openApi, "specKind");
        StringAssert.Contains(openApi, "DirectFlowSpec");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/flows/direct-sql");
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/flows/direct-sql")).StatusCode);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static FlowDefinition Definition(string name, FlowKind kind, FlowSpec spec) => new(new FlowId(name), name, null, kind, "1.0.0", true, null, spec, new Dictionary<string, string>(), Now, Now);

    private sealed class FlowFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;
        public FlowService Service => _provider.GetRequiredService<FlowService>();
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
}
