using System.Globalization;
using System.Net;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CleanupConsoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
    private CultureInfo? originalCulture;
    private CultureInfo? originalUiCulture;

    [TestInitialize]
    public void SetEnglishCulture()
    {
        originalCulture = CultureInfo.CurrentCulture;
        originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    [TestCleanup]
    public void RestoreCulture()
    {
        CultureInfo.CurrentCulture = originalCulture!;
        CultureInfo.CurrentUICulture = originalUiCulture!;
    }

    [TestMethod]
    public async Task CleanupPageDeletesSelectionsInDependencyOrderWithExplicitConfirmation()
    {
        using var context = CreateContext(AuthorizationPermissions.ResourcesDelete, AuthorizationPermissions.RunsDelete);
        var client = new FakeCleanupApiClient(Inventory());
        context.Services.AddSingleton<ICleanupApiClient>(client);
        var rendered = context.Render<Cleanup>();

        Assert.HasCount(5, rendered.FindAll(".cleanup-metric"));
        Assert.HasCount(6, rendered.FindAll(".cleanup-row"));
        Assert.AreEqual(string.Empty, rendered.Find("input[type='search']").GetAttribute("value") ?? string.Empty);
        var entriesPanel = rendered.Find("section[data-kind='Entries']");
        var flowsPanel = rendered.Find("section[data-kind='Flows']");
        Assert.HasCount(1, entriesPanel.QuerySelectorAll(".cleanup-entry-options"));
        Assert.HasCount(1, flowsPanel.QuerySelectorAll(".cleanup-flow-options"));
        await rendered.Find(".cleanup-select-everything input").ChangeAsync(new() { Value = true });
        Assert.IsTrue(rendered.Find(".cleanup-select-everything input").HasAttribute("checked"));
        Assert.IsTrue(entriesPanel.QuerySelectorAll(".cleanup-entry-options input[type='checkbox']").All(input => input.HasAttribute("checked")));
        Assert.IsTrue(flowsPanel.QuerySelector(".cleanup-flow-options input[type='checkbox']")!.HasAttribute("checked"));
        await rendered.Find("[data-testid='open-cleanup-confirmation']").ClickAsync(new());

        var confirm = rendered.Find("[data-testid='confirm-cleanup']");
        Assert.IsTrue(confirm.HasAttribute("disabled"));
        await rendered.Find(".cleanup-acknowledgement input").ChangeAsync(new() { Value = true });
        await rendered.Find("[data-testid='confirm-cleanup']").ClickAsync(new());

        CollectionAssert.AreEqual(
            new[] { CleanupResourceKind.RuntimeRun, CleanupResourceKind.FlowRun, CleanupResourceKind.Task, CleanupResourceKind.Entry, CleanupResourceKind.Flow, CleanupResourceKind.Agent },
            client.Deleted.Select(candidate => candidate.Kind).ToArray());
        Assert.IsTrue(client.EntryOptions.Single().RemoveDashboardReferences);
        Assert.IsTrue(client.EntryOptions.Single().CloseInteractions);
        Assert.IsTrue(client.FlowOptions.Single().DeleteSystemManagedFlows);
        Assert.AreEqual(0, rendered.FindAll(".cleanup-row").Count);
        StringAssert.Contains(rendered.Markup, "0 items selected");
    }

    [TestMethod]
    public void CleanupPageDoesNotLoadInventoryWithoutBothDeletionPermissions()
    {
        using var context = CreateContext(AuthorizationPermissions.ResourcesDelete);
        var client = new FakeCleanupApiClient(Inventory());
        context.Services.AddSingleton<ICleanupApiClient>(client);

        var rendered = context.Render<Cleanup>();

        Assert.AreEqual(0, client.LoadCount);
        StringAssert.Contains(rendered.Markup, "requires both resource deletion and Run deletion permissions");
    }

    [TestMethod]
    public async Task CleanupClientUsesCanonicalDeleteRoutesAndCurrentETags()
    {
        var handler = new RecordingHandler();
        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            [CleanupApiClient.ManagementClient] = Client(handler),
            [CleanupApiClient.RuntimeClient] = Client(handler),
            [CleanupApiClient.FlowClient] = Client(handler),
            [CleanupApiClient.WorkClient] = Client(handler)
        };
        var api = new CleanupApiClient(new TestHttpClientFactory(clients));
        var options = new CleanupDeletionOptions(true, true, true);

        foreach (var candidate in Inventory().All)
            await api.DeleteAsync(candidate, options, default);

        var deletes = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            "/api/runtime/runs/runtime-1",
            "/api/flowRuns/flow-run-1",
            $"/api/tasks/{TaskId:D}",
            "/api/management/entries/entry-1?removeDashboardReferences=true&closeInteractions=true",
            "/api/namespaces/team-a/flows/flow-1?deleteSystemManaged=true",
            "/api/namespaces/team-a/agents/agent-1"
        }, deletes.Select(request => request.Path).ToArray());
        Assert.IsTrue(deletes.Where(request => !request.Path.Contains("/entries/", StringComparison.Ordinal)).All(request => request.IfMatch == "\"current\""));

        handler.Requests.Clear();
        await api.DeleteAsync(Inventory().Flows.Single(), new CleanupDeletionOptions(false, false, false), default);
        Assert.AreEqual("/api/namespaces/team-a/flows/flow-1", handler.Requests.Single(request => request.Method == HttpMethod.Delete).Path);
    }

    private static BunitContext CreateContext(params string[] permissions)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<IConsoleContextProvider>(new TestContextProvider(permissions));
        context.Services.AddAgentstrationWebComponents();
        return context;
    }

    private static CleanupInventory Inventory()
    {
        var team = new ResourceNamespace("team-a");
        return new(
            [
                new(CleanupResourceKind.RuntimeRun, "runtime-1", "Agent run", ResourceNamespace.Default, "Succeeded", Now, "agent-1"),
                new(CleanupResourceKind.FlowRun, "flow-run-1", "Flow run", ResourceNamespace.Default, "Failed", Now.AddMinutes(-1), "1.0.0")
            ],
            [new(CleanupResourceKind.Task, TaskId.ToString("D"), "Task one", ResourceNamespace.Default, "Completed", Now.AddMinutes(-2), "entry-1")],
            [new(CleanupResourceKind.Entry, "entry-1", "Entry one", ResourceNamespace.Default, "Published", Now, "flow-1")],
            [new(CleanupResourceKind.Flow, "flow-1", "Flow one", team, "Active", Now, "1.0.0")],
            [new(CleanupResourceKind.Agent, "agent-1", "Agent one", team, "Accepted", Now, "model-default")]);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };

    private sealed class TestContextProvider(IReadOnlyCollection<string> permissions) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace",
            permissions.ToHashSet(StringComparer.Ordinal), []));
    }

    private sealed class FakeCleanupApiClient(CleanupInventory initial) : ICleanupApiClient
    {
        private readonly List<CleanupCandidate> remaining = [.. initial.All];
        public List<CleanupCandidate> Deleted { get; } = [];
        public List<CleanupDeletionOptions> EntryOptions { get; } = [];
        public List<CleanupDeletionOptions> FlowOptions { get; } = [];
        public int LoadCount { get; private set; }

        public Task<CleanupInventory> GetInventoryAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(new CleanupInventory(
                remaining.Where(candidate => candidate.Kind is CleanupResourceKind.RuntimeRun or CleanupResourceKind.FlowRun).ToArray(),
                remaining.Where(candidate => candidate.Kind == CleanupResourceKind.Task).ToArray(),
                remaining.Where(candidate => candidate.Kind == CleanupResourceKind.Entry).ToArray(),
                remaining.Where(candidate => candidate.Kind == CleanupResourceKind.Flow).ToArray(),
                remaining.Where(candidate => candidate.Kind == CleanupResourceKind.Agent).ToArray()));
        }

        public Task DeleteAsync(CleanupCandidate candidate, CleanupDeletionOptions options, CancellationToken cancellationToken)
        {
            Deleted.Add(candidate);
            if (candidate.Kind == CleanupResourceKind.Entry) EntryOptions.Add(options);
            if (candidate.Kind == CleanupResourceKind.Flow) FlowOptions.Add(options);
            remaining.RemoveAll(value => value.Key == candidate.Key);
            return Task.CompletedTask;
        }
    }

    private static readonly Guid TaskId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? IfMatch);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.PathAndQuery, request.Headers.IfMatch.SingleOrDefault()?.Tag));
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method == HttpMethod.Get) response.Headers.ETag = new("\"current\"");
            return Task.FromResult(response);
        }
    }

    private sealed class TestHttpClientFactory(IReadOnlyDictionary<string, HttpClient> clients) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients[name];
    }
}
