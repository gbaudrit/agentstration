using System.Text.Json;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.Tests;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Console.Components.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConsoleConversationsTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ListsAuthorizedConsoleEntryConversationsAndBuildsReplayLinks()
    {
        using var culture = new TestCultureScope("en-US");
        var assistant = Entry("assistant", "Assistant");
        var report = Entry("report", "Report builder");
        var hidden = Interaction("hidden", InteractionStatus.Active, Now.AddMinutes(1), "must not leak");
        using var context = CreateContext(
            [assistant, report],
            [
                Interaction("assistant", InteractionStatus.WaitingForUser, Now.AddMinutes(3), "Need a confirmation", pending: true),
                hidden,
                Interaction("report", InteractionStatus.Completed, Now.AddMinutes(2), "Prepare the monthly report")
            ],
            out var client);

        var rendered = context.Render<ConsoleConversations>();

        var rows = rendered.WaitForElements("[data-testid='conversation-row']", 2);
        Assert.AreEqual("assistant", rows[0].GetAttribute("data-entry-name"));
        Assert.AreEqual("WaitingForUser", rows[0].GetAttribute("data-status"));
        StringAssert.Contains(rows[0].TextContent, "Needs your input");
        Assert.AreEqual("report", rows[1].GetAttribute("data-entry-name"));
        Assert.IsFalse(rendered.Markup.Contains("must not leak", StringComparison.Ordinal));
        Assert.AreEqual(WorkspaceId, client.ListWorkspaceId);
        Assert.AreEqual(50, client.ListTake);
        Assert.AreEqual(0, rendered.FindAll(".realtime-status").Count);
        Assert.IsFalse(rendered.Markup.Contains("Default workspace", StringComparison.Ordinal));

        var expected = ConsoleEntryInteractionNavigation.Build(
            WorkspaceId,
            new ResourceNamespace("tools"),
            "assistant",
            interactionId: Guid.Parse(rows[0].GetAttribute("data-interaction-id")!));
        Assert.AreEqual(expected, rows[0].GetAttribute("href"));
    }

    [TestMethod]
    public void EmptyAndUnavailableStatesAreExplicit()
    {
        using var culture = new TestCultureScope("en-US");
        using var emptyContext = CreateContext([Entry("assistant", "Assistant")], [], out _);
        var empty = emptyContext.Render<ConsoleConversations>();
        empty.WaitForAssertion(() => StringAssert.Contains(empty.Markup, "No conversations yet"));

        using var unavailableContext = CreateContext([Entry("assistant", "Assistant")], [], out var unavailableClient);
        unavailableClient.Error = new HttpRequestException("offline");
        var unavailable = unavailableContext.Render<ConsoleConversations>();
        unavailable.WaitForAssertion(() => StringAssert.Contains(unavailable.Markup, "Conversations unavailable"));
    }

    [TestMethod]
    public void MainNavigationShowsConversationsOnlyWithRunReadPermission()
    {
        using var allowed = CreateContext([], [], out _, new HashSet<string>(["runs/read"], StringComparer.Ordinal));
        var allowedLayout = allowed.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));
        allowedLayout.WaitForElement("a[href='/conversations']");

        using var denied = CreateContext([], [], out _, new HashSet<string>(StringComparer.Ordinal));
        var deniedLayout = denied.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));
        deniedLayout.WaitForAssertion(() => Assert.AreEqual(0, deniedLayout.FindAll("a[href='/conversations']").Count));
    }

    [TestMethod]
    public void ConversationListReturnsToThePersistedGenericInteraction()
    {
        var interaction = Interaction("assistant", InteractionStatus.Completed, Now, "Persisted request");
        using var context = CreateContext([Entry("assistant", "Assistant")], [interaction], out _);
        var list = context.Render<ConsoleConversations>();
        var replayUrl = list.WaitForElement("[data-testid='conversation-row']").GetAttribute("href")!;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(replayUrl);

        var resumed = context.Render<ConsoleEntryInteraction>(parameters => parameters
            .Add(value => value.WorkspaceId, WorkspaceId)
            .Add(value => value.EntryNamespace, "tools")
            .Add(value => value.EntryName, "assistant"));

        resumed.WaitForElement("[data-testid='workplace-conversation-message']");
        StringAssert.Contains(resumed.Markup, "Persisted request");
        Assert.AreEqual("/conversations", resumed.Find("[data-testid='back-to-conversations']").GetAttribute("href"));
        Assert.AreEqual(0, resumed.FindAll(".page-actions .realtime-status").Count);
    }

    private static BunitContext CreateContext(
        IReadOnlyList<EntryResponse> entries,
        IReadOnlyList<InteractionResponse> interactions,
        out InteractionClient interactionClient,
        IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddAgentstrationWebComponents();
        var contextProvider = new ContextProvider(permissions ?? new HashSet<string>(["runs/read"], StringComparer.Ordinal));
        context.Services.AddSingleton<IConsoleContextProvider>(contextProvider);
        context.Services.AddSingleton(new ConsoleContextState(contextProvider));
        context.Services.AddSingleton<IEntryAdministrationApiClient>(new EntryClient(entries));
        interactionClient = new(interactions);
        context.Services.AddSingleton<IConsoleEntryInteractionApiClient>(interactionClient);
        context.Services.AddSingleton<IWorkOperationsRealtimeClient>(new RealtimeClient());
        return context;
    }

    private static EntryResponse Entry(string name, string displayName) => new(
        WorkspaceId, name, name, WorkResourceTypes.Entries, WorkplaceApiVersions.CoreV1, displayName, null,
        new EntryPresentation { Kind = EntryPresentationKind.Prompt, Fields = [new() { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }] },
        new EntryExposure { Surfaces = [EntryExposureSurface.Console], WorkplacePlacements = [] },
        new("flow", "1.0.0"), new(), 1, Now)
    {
        Namespace = new("tools"),
        Execution = new(true, EntryExecutionAvailability.Executable, null)
    };

    private static InteractionResponse Interaction(string entryName, InteractionStatus status, DateTimeOffset activity, string message, bool pending = false)
    {
        var id = Guid.NewGuid();
        return new(id, WorkspaceId, entryName, status, activity.AddMinutes(-1), activity,
            new Dictionary<string, JsonElement>(), [],
            [new(Guid.NewGuid(), new(WorkspaceId), new(id), null, ConversationRole.User, message, activity.AddMinutes(-1))],
            pending ? Guid.NewGuid() : null, null, null, 1)
        { EntryNamespace = new("tools") };
    }

    private sealed class ContextProvider(IReadOnlySet<string> permissions) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "User", Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "dev", "Development", WorkspaceId, "default", "Default Workspace", permissions,
            [new(WorkspaceId, Guid.Parse("33333333-3333-3333-3333-333333333333"), "dev", "Development", "default", "Default Workspace")]));
    }

    private sealed class EntryClient(IReadOnlyList<EntryResponse> entries) : IEntryAdministrationApiClient
    {
        public Task<IReadOnlyList<EntryResponse>> GetExposedEntriesAsync(EntryExposureSurface surface, EntryWorkplacePlacement? placement, CancellationToken cancellationToken) => Task.FromResult(entries);
        public Task<IReadOnlyList<EntryDraftResponse>> GetEntriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryDraftResponse> GetEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryDraft> SaveEntryAsync(EntryDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryValidationResponse> ValidateEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryResource> PublishEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourcePickerItem>> GetResourcesAsync(EntryBindingKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntryResponse>> GetPublishedEntriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceDashboardDraftResponse>> GetDashboardsAsync(string workspaceName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboardDraftResponse> GetDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboardDraft> SaveDashboardAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboard> PublishDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InteractionClient(IReadOnlyList<InteractionResponse> interactions) : IConsoleEntryInteractionApiClient
    {
        public Exception? Error { get; set; }
        public Guid ListWorkspaceId { get; private set; }
        public int ListTake { get; private set; }
        public Task<IReadOnlyList<InteractionResponse>> ListInteractionsAsync(Guid workspaceId, int take, CancellationToken cancellationToken)
        {
            ListWorkspaceId = workspaceId;
            ListTake = take;
            return Error is null ? Task.FromResult(interactions) : Task.FromException<IReadOnlyList<InteractionResponse>>(Error);
        }
        public Task<InteractionResponse> GetInteractionAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult(interactions.Single(value => value.Id == interactionId));
        public Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult(interactions.Single(value => value.Id == interactionId).Messages);
        public Task<IReadOnlyList<PendingActionContract>> ListPendingActionsAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PendingActionContract>>([]);
        public Task<AddConversationMessageResponse> AddMessageAsync(Guid workspaceId, Guid interactionId, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PendingActionResolutionResponse> RespondAsync(Guid workspaceId, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskResponse> GetTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskResponse> CancelTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskActivity>>([]);
        public Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskResult>>([]);
        public Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskArtifact>>([]);
        public Uri GetArtifactContentUri(Guid workspaceId, Guid taskId, Guid artifactId) => new("http://localhost/artifact");
    }

    private sealed class RealtimeClient : IWorkOperationsRealtimeClient
    {
        public WorkOperationsRealtimeState State => WorkOperationsRealtimeState.Live;
        public event Func<WorkOperationsRealtimeUpdate, Task>? Updated { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public Task StartAsync(IEnumerable<string> workspaceIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
