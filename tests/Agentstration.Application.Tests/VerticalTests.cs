using Agentstration.Application;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Memory;
using Agentstration.Application.Missions;
using Agentstration.Application.Routing;
using Agentstration.Application.Workflows;
using Agentstration.Application.Workspaces;
using Agentstration.Contracts;
using Agentstration.Domain;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Missions;
using Agentstration.Infrastructure.Persistence;
using Agentstration.Web.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using ModelContextProtocol.Server;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class VerticalTests
{
    [TestMethod]
    public async Task WorkspaceAndInboxCreationPreserveWorkspaceIsolation()
    {
        var fixture = new Fixture();
        var first = (await fixture.Workspaces.CreateAsync("First workspace", default)).Value!;
        var second = (await fixture.Workspaces.CreateAsync("Second workspace", default)).Value!;
        var inbox = (await fixture.Workspaces.CreateInboxAsync(first.Id, new CreateInboxRequest("Documents", null, null), default)).Value!.Inbox;

        Assert.HasCount(1, await fixture.Store.ListInboxesAsync(first.Id, default));
        Assert.IsEmpty(await fixture.Store.ListInboxesAsync(second.Id, default));
        Assert.AreEqual(first.Id, inbox.WorkspaceId);
    }

    [TestMethod]
    public async Task TextIngestionIsIdempotentPreservesRawContentAndPublishesEvent()
    {
        var fixture = await Fixture.WithWorkspaceAsync();
        var first = await fixture.Ingestion.IngestAsync(fixture.Workspace!.Id, fixture.Inbox!.Id, "Original agent document", null, "external-1", "text/plain", default);
        var second = await fixture.Ingestion.IngestAsync(fixture.Workspace.Id, fixture.Inbox.Id, "Original agent document", null, "external-1", "text/plain", default);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.Value!.Duplicate);
        Assert.AreEqual(first.Value!.ItemId, second.Value.ItemId);
        Assert.AreEqual("Original agent document", (await fixture.Store.GetRawContentAsync(fixture.Workspace.Id, first.Value.ItemId, default))!.Value);
        Assert.HasCount(1, fixture.Bus.Events.OfType<ItemReceived>().ToArray());
    }

    [TestMethod]
    public async Task DeterministicRouterSelectsContentWorkflow()
    {
        var router = new DeterministicIntentRouter();
        var workspaceId = WorkspaceId.New();
        var item = new Item(ItemId.New(), workspaceId, InboxId.New(), "text/plain", "hash", null, ItemStatus.Queued, DateTimeOffset.UtcNow);
        var decision = await router.RouteAsync(new RoutingContext(workspaceId, item, new RawContent(item.Id, workspaceId, "text", "text/plain", null, DateTimeOffset.UtcNow)), default);
        Assert.AreEqual("content-processing", decision.Route);
        Assert.IsFalse(decision.StoreOnly);
    }

    [TestMethod]
    public async Task WorkflowNormalizesSummarizesCategorizesAndStoresMemory()
    {
        var fixture = await Fixture.WithWorkspaceAsync();
        var result = await fixture.Ingestion.IngestAsync(fixture.Workspace!.Id, fixture.Inbox!.Id, "<h1>AI agent roadmap</h1>   milestone one", null, null, "text/plain", default);
        await fixture.Workflow.ExecuteAsync(fixture.Workspace.Id, result.Value!.ItemId, default);

        var details = (await fixture.Ingestion.GetAsync(fixture.Workspace.Id, result.Value.ItemId, default)).Value!;
        Assert.AreEqual("AI agent roadmap milestone one", details.Normalized!.Value);
        Assert.AreEqual(ItemStatus.Processed, details.Item.Status);
        Assert.HasCount(1, details.Memory);
        CollectionAssert.Contains(details.Memory[0].Categories.ToArray(), "artificial intelligence");
        Assert.HasCount(1, await fixture.Memory.SearchAsync(fixture.Workspace.Id, "roadmap", 20, default));
    }

    [TestMethod]
    public async Task AgentFailureMarksItemAsFailedWithoutOverwritingRawContent()
    {
        var fixture = await Fixture.WithWorkspaceAsync(new FailingRuntime());
        var result = await fixture.Ingestion.IngestAsync(fixture.Workspace!.Id, fixture.Inbox!.Id, "keep me", null, null, "text/plain", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Workflow.ExecuteAsync(fixture.Workspace.Id, result.Value!.ItemId, default));
        var details = (await fixture.Ingestion.GetAsync(fixture.Workspace.Id, result.Value!.ItemId, default)).Value!;
        Assert.AreEqual(ItemStatus.Failed, details.Item.Status);
        Assert.AreEqual("keep me", details.Raw.Value);
    }

    [TestMethod]
    public async Task MonitoringMissionRecordsChangesAndCreatesThresholdNotification()
    {
        var fixture = await Fixture.WithWorkspaceAsync();
        var mission = (await fixture.Missions.CreateAsync(fixture.Workspace!.Id, new CreateMissionRequest("Price", "Watch demo", "demo://product/1", 60, 300), default)).Value!;
        await fixture.Missions.RunAsync(fixture.Workspace.Id, mission.Id, default);
        await fixture.Missions.RunAsync(fixture.Workspace.Id, mission.Id, default);
        var third = await fixture.Missions.RunAsync(fixture.Workspace.Id, mission.Id, default);

        Assert.AreEqual(299m, third.Value!.Observation);
        Assert.IsTrue(third.Value.Changed);
        Assert.HasCount(3, await fixture.Store.ListMissionRunsAsync(fixture.Workspace.Id, mission.Id, default));
        Assert.HasCount(1, await fixture.Store.ListNotificationsAsync(fixture.Workspace.Id, mission.Id, default));
        Assert.IsGreaterThan(DateTimeOffset.UtcNow, (await fixture.Store.GetMissionAsync(fixture.Workspace.Id, mission.Id, default))!.NextRunAt);
    }

    [TestMethod]
    public void McpExposesTheRequiredToolSet()
    {
        var names = typeof(PlatformMcpTools).GetMethods()
            .Select(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), false).Cast<McpServerToolAttribute>().FirstOrDefault()?.Name)
            .Where(name => name is not null).ToArray();
        var required = new[] { "list_workspaces", "list_inboxes", "ingest_text", "ingest_url", "search_memory", "create_mission", "get_mission", "list_mission_runs", "run_mission_now" };
        foreach (var tool in required) CollectionAssert.Contains(names, tool);
    }

    [TestMethod]
    public async Task RestApiStartsAndReturnsSeedWorkspace()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/workspaces");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Demo workspace", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        public InMemoryPlatformStore Store { get; } = new();
        public RecordingBus Bus { get; } = new();
        public WorkspaceService Workspaces { get; }
        public IngestionService Ingestion { get; }
        public MemoryService Memory { get; }
        public ContentProcessingWorkflow Workflow { get; }
        public MissionService Missions { get; }
        public Workspace? Workspace { get; private set; }
        public Inbox? Inbox { get; private set; }

        public Fixture(IAgentRuntime? runtime = null)
        {
            Workspaces = new WorkspaceService(Store, TimeProvider.System);
            Ingestion = new IngestionService(Store, Bus, new StubContentReader(), TimeProvider.System);
            Memory = new MemoryService(Store);
            runtime ??= new MicrosoftExtensionsAiAgentRuntime(new DeterministicChatClient());
            Workflow = new ContentProcessingWorkflow(Store, new DeterministicIntentRouter(), runtime, Memory, Bus, TimeProvider.System);
            Missions = new MissionService(Store, new DemoObservationTool(), Bus, TimeProvider.System);
        }

        public static async Task<Fixture> WithWorkspaceAsync(IAgentRuntime? runtime = null)
        {
            var fixture = new Fixture(runtime);
            fixture.Workspace = (await fixture.Workspaces.CreateAsync("Test workspace", default)).Value!;
            fixture.Inbox = (await fixture.Workspaces.CreateInboxAsync(fixture.Workspace.Id, new CreateInboxRequest("Test inbox", null, null), default)).Value!.Inbox;
            return fixture;
        }
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<IDomainEvent> Events { get; } = [];
        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken) where TEvent : IDomainEvent { Events.Add(domainEvent); return Task.CompletedTask; }
    }

    private sealed class StubContentReader : IContentSourceReader { public Task<string> ReadUrlAsync(Uri uri, CancellationToken cancellationToken) => Task.FromResult($"Content from {uri}"); }
    private sealed class FailingRuntime : IAgentRuntime { public Task<AgentExecutionResult> RunAsync(AgentExecutionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Agent failed safely."); }
}
