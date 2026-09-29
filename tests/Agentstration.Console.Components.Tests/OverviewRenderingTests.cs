using Agentstration.Flows;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
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
            new StubModelProvidersClient(),
            new StubExtensionsClient(),
            NullLogger<PlatformDashboardService>.Instance));
        var notifications = new NotificationState();
        notifications.Add(new NotificationItem(Guid.NewGuid(), "Review required", "A resource changed.", DateTimeOffset.UtcNow, UiStatus.Warning));
        context.Services.AddSingleton(notifications);
        context.Services.AddSingleton<IAgentstrationEventStream>(eventStream);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            var groups = rendered.FindAll(".overview-metric-group");
            Assert.HasCount(3, groups);
            CollectionAssert.AreEqual(
                new[] { "Build / Configure", "Operate", "Supervise" },
                groups.Select(group => group.QuerySelector("h2")!.TextContent.Trim()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "Defined agents", "Defined flows", "Extensions", "Model providers" },
                groups[0].QuerySelectorAll(".metric-label").Select(label => label.TextContent.Trim()).ToArray());
            Assert.IsEmpty(rendered.FindAll(".dashboard-sources"));
            StringAssert.Contains(rendered.Find(".dashboard-grid").TextContent, "Loading run events…");
        });
        var notificationsCard = rendered.Find(".metric-card-button");
        Assert.AreEqual("Notifications", notificationsCard.QuerySelector(".metric-label")!.TextContent.Trim());
        Assert.AreEqual("1", notificationsCard.QuerySelector("strong")!.TextContent.Trim());
        notificationsCard.Click();
        Assert.IsTrue(notifications.IsPanelOpen);
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
    public void OverviewLocalizesMetricDetailsInFrench()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var api = new MockApiClient(TimeProvider.System);
        var eventStream = new ControlledEventStream();
        eventStream.Complete([]);
        context.Services.AddSingleton(new PlatformDashboardService(
            api,
            api,
            new StubWorkClient(),
            api,
            new StubModelProvidersClient(),
            new StubExtensionsClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton<NotificationState>();
        context.Services.AddSingleton<IAgentstrationEventStream>(eventStream);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            var details = rendered.FindAll(".overview-metric-grid .metric-card small")
                .Select(detail => detail.TextContent.Trim())
                .ToArray();
            CollectionAssert.Contains(details, "Prêts / souhaités");
            Assert.IsTrue(details.Any(detail => detail.EndsWith(" en attente d’une saisie", StringComparison.Ordinal)));
            Assert.IsTrue(details.Any(detail => detail.EndsWith(" échec(s) lors de la dernière exécution", StringComparison.Ordinal)));
            Assert.IsTrue(details.Any(detail => detail.EndsWith(" indisponible(s)", StringComparison.Ordinal)));
            StringAssert.Contains(rendered.Markup, "Aucun événement d’exécution");
            StringAssert.Contains(rendered.Markup, "Les événements persistants du cycle de vie du Runtime et des Flows apparaîtront ici.");
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
            new StubModelProvidersClient(),
            new StubExtensionsClient(),
            NullLogger<PlatformDashboardService>.Instance));
        context.Services.AddSingleton<NotificationState>();
        context.Services.AddSingleton<IAgentstrationEventStream>(new ControlledEventStream());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<Agentstration.Web.Components.Pages.Home>();

        rendered.WaitForAssertion(() =>
        {
            var cards = rendered.FindAll(".overview-metric-grid > *");
            var agents = cards[0];
            var tasks = cards[7];
            Assert.IsFalse(agents.ClassList.Contains("metric-card-loading"));
            Assert.IsTrue(tasks.ClassList.Contains("metric-card-loading"));
            Assert.IsTrue(work.IsSummaryPending);
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

    private sealed class StubExtensionsClient : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExtensionResponse>>([]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
