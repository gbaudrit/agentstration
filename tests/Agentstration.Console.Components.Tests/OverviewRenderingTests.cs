using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Flows;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;
using Agentstration.Web.Components.State;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OverviewRenderingTests
{
    [TestMethod]
    public void OverviewDisplaysMetricsWhileRunEventsAreStillLoading()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var api = new MockApiClient(TimeProvider.System);
        var eventStream = new ControlledEventStream();
        context.Services.AddSingleton(new PlatformDashboardService(
            api,
            api,
            new StubWorkClient(),
            api,
            new StubExtensionsClient(),
            new StubModelProvidersClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IAgentstrationEventStream>(eventStream);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(rendered.Find(".overview-metric-grid"));
            StringAssert.Contains(rendered.Find(".dashboard-grid").TextContent, "Loading run events…");
        });
        Assert.IsFalse(eventStream.IsCompleted);

        eventStream.Complete([
            new EventListItem(DateTimeOffset.UtcNow, "Info", "Runtime Run run-1", "Completed", "Run completed", Url: "/runs/run-1")
        ]);

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Run completed", rendered.Find(".event-list strong").TextContent);
            Assert.AreEqual("/runs/run-1", rendered.Find(".event-list-resource").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void RunEventFailureRemainsConfinedToItsPanel()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IAgentstrationEventStream>(new FailingEventStream());

        var rendered = context.Render<Agentstration.Web.Components.LatestRunEvents>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Run events unavailable");
            Assert.AreEqual("Latest run events", rendered.Find(".panel-header h2").TextContent);
        });
    }

    [TestMethod]
    public void OverviewDisplaysReadyTilesWithoutWaitingForSlowTiles()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var api = new MockApiClient(TimeProvider.System);
        var work = new StubWorkClient();
        work.DelayNextSummary();
        context.Services.AddSingleton(new PlatformDashboardService(
            api,
            api,
            work,
            api,
            new StubExtensionsClient(),
            new StubModelProvidersClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IAgentstrationEventStream>(new ControlledEventStream());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            var cards = rendered.FindAll(".overview-metric-grid .metric-card");
            var agents = cards[0];
            var tasks = cards[7];
            Assert.IsFalse(agents.ClassList.Contains("metric-card-loading"));
            Assert.IsTrue(tasks.ClassList.Contains("metric-card-loading"));
            Assert.IsTrue(work.IsSummaryPending);
        });
    }

    [TestMethod]
    public void OverviewGroupsMetricsAndRemovesDuplicateSourceNavigation()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var api = new MockApiClient(TimeProvider.System);
        var notifications = new NotificationState();
        notifications.Add(new(Guid.NewGuid(), "Update ready", "A resource changed.", DateTimeOffset.UtcNow, UiStatus.Info));
        context.Services.AddSingleton(new PlatformDashboardService(
            api,
            api,
            new StubWorkClient(),
            api,
            new StubExtensionsClient(2),
            new StubModelProvidersClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton(notifications);
        context.Services.AddSingleton<IAgentstrationEventStream>(new ControlledEventStream());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            var rows = rendered.FindAll(".overview-metric-row");
            Assert.HasCount(3, rows);
            CollectionAssert.AreEqual(
                new[] { "Defined agents", "Defined flows", "Extensions", "Model providers" },
                rows[0].QuerySelectorAll(".metric-label").Select(item => item.TextContent.Trim()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "Enabled triggers", "Agent runs", "Flow runs", "Tasks running" },
                rows[1].QuerySelectorAll(".metric-label").Select(item => item.TextContent.Trim()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "Ready deployments", "Needs attention", "Notifications" },
                rows[2].QuerySelectorAll(".metric-label").Select(item => item.TextContent.Trim()).ToArray());
            Assert.AreEqual("2", rows[0].QuerySelectorAll(".metric-card strong")[2].TextContent.Trim());
            Assert.AreEqual("1", rows[2].QuerySelectorAll(".metric-card strong")[2].TextContent.Trim());
            Assert.AreEqual("/#notifications", rows[2].QuerySelectorAll(".metric-card")[2].GetAttribute("href"));
            Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find(".dashboard-sources"));
        });
    }

    [TestMethod]
    public void AttentionTileAndItemsExposeKeyboardAccessibleNavigation()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var api = new MockApiClient(TimeProvider.System);
        context.Services.AddSingleton(new PlatformDashboardService(
            new StubManagementClientWithAttention(),
            api,
            new StubWorkClient(),
            api,
            new StubExtensionsClient(),
            new StubModelProvidersClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IAgentstrationEventStream>(new ControlledEventStream());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("/#needs-attention", rendered.Find("a.metric-card[href='/#needs-attention']").GetAttribute("href"));
            var attention = rendered.Find("#needs-attention");
            Assert.AreEqual("-1", attention.GetAttribute("tabindex"));
            Assert.AreEqual("/deployments", attention.QuerySelector("a.attention-item")?.GetAttribute("href"));
        });
    }

    private sealed class ControlledEventStream : IAgentstrationEventStream
    {
        private readonly TaskCompletionSource<IReadOnlyList<EventListItem>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCompleted => completion.Task.IsCompleted;

        public async Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken) =>
            await completion.Task.WaitAsync(cancellationToken);

        public void Complete(IReadOnlyList<EventListItem> events) => completion.SetResult(events);
    }

    private sealed class FailingEventStream : IAgentstrationEventStream
    {
        public Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<EventListItem>>(new HttpRequestException("Unavailable"));
    }

    private sealed class StubWorkClient : IWorkApiClient
    {
        private TaskCompletionSource<WorkTaskOperationsCountersResponse>? delayedSummary;

        public bool IsSummaryPending => delayedSummary is { Task.IsCompleted: false };

        public void DelayNextSummary() => delayedSummary =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken) =>
            delayedSummary is null
                ? Task.FromResult(new WorkTaskOperationsCountersResponse(0, 0, 0, 0, 0))
                : delayedSummary.Task.WaitAsync(cancellationToken);
        public Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PendingActionContract> RespondTaskPendingActionAsync(Guid taskId, Guid actionId, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubModelProvidersClient : IModelProvidersClient
    {
        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelProviderResponse>>([]);
        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubExtensionsClient(int count = 0) : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionResponse>>([]);
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>(Enumerable.Range(0, count).Select(index => new ExtensionInventoryItemResponse(
                $"extension-{index}", $"Extension {index}", "default", null, null, $"Extension {index}", $"extension-{index}", "1.0.0", new Uri("http://localhost"), "configured", true, "available", null, null, null, [])).ToArray());
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubManagementClientWithAttention : IManagementApiClient
    {
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentSummary>>([]);
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeploymentSummary>>([
            new("deployment-a", "agent-a", "default", "Failed", "Running", "local", "local", "default", "revision-1", "revision-1", DateTimeOffset.UtcNow, "Runtime unavailable")
        ]);
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriggerResource>>([]);
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
