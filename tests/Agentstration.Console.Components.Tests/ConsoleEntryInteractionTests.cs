using System.Text.Json;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Console.Components.Tests;

[TestClass]
public sealed class ConsoleEntryInteractionTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid InteractionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid TaskId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void DirectOpeningRendersTheGenericEntryWithoutSubmitting()
    {
        using var context = CreateContext(out var entries, out _);

        var rendered = Render(context);

        rendered.WaitForElement("[data-testid='workplace-prompt-input']");
        Assert.IsNull(entries.LastSubmission);
        Assert.AreEqual(WorkspaceId.ToString("D"), rendered.Find("[data-testid='console-entry-scope']").GetAttribute("data-owner-workspace"));
    }

    [TestMethod]
    public async Task PaletteFallbackCarriesTheExactQueryIntoTheOwningWorkspaceInteraction()
    {
        const string query = "  explique & diagnostique ce Flow  ";
        using var context = CreateContext(out var entries, out _);
        var fallback = new ConsoleEntryCommandFallbackProvider(entries, NullLogger<ConsoleEntryCommandFallbackProvider>.Instance);
        context.Services.AddSingleton<IResourceSearchProvider>(new EmptyResourceSearchProvider());
        context.Services.AddSingleton<ICommandPaletteFallbackProvider>(fallback);

        var palette = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));
        await palette.Find("[data-testid='console-command-trigger']").ClickAsync(new());
        await palette.Find("[data-testid='console-command-input']").InputAsync(new ChangeEventArgs { Value = query });
        var fallbackAction = palette.WaitForElement("[data-testid='console-entry-fallback']");
        Assert.AreEqual("http://localhost/", context.Services.GetRequiredService<NavigationManager>().Uri, "Typing must not execute the Entry.");

        await fallbackAction.ClickAsync(new());

        var uri = new Uri(context.Services.GetRequiredService<NavigationManager>().Uri);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var rendered = context.Render<ConsoleEntryInteraction>(parameters => parameters
            .Add(value => value.WorkspaceId, Guid.Parse(segments[1]))
            .Add(value => value.EntryNamespace, segments[2])
            .Add(value => value.EntryName, segments[3]));

        rendered.WaitForElement("[data-testid='workplace-interaction']");
        Assert.IsNotNull(entries.LastSubmission);
        Assert.AreEqual(WorkspaceId, entries.LastSubmission.Value.WorkspaceId);
        Assert.AreEqual(new ResourceNamespace("tools"), entries.LastSubmission.Value.Namespace);
        Assert.AreEqual("assistant", entries.LastSubmission.Value.Name);
        Assert.AreEqual(query, entries.LastSubmission.Value.Request.Values["request"].GetString());
    }

    [TestMethod]
    public void DifferentEntriesUseTheSameGenericInteractionSurface()
    {
        foreach (var name in new[] { "assistant", "report-builder" })
        {
            using var context = CreateContext(out _, out _, explicitEntries: [Entry(true, name)]);
            var rendered = Render(context, entryName: name);

            rendered.WaitForElement("[data-testid='workplace-prompt-input']");
            Assert.AreEqual(name, rendered.Find("[data-testid='console-entry-interaction']").GetAttribute("data-entry-name"));
        }
    }

    [TestMethod]
    public void DisabledAndUnavailableEntriesFailClosed()
    {
        using var disabledContext = CreateContext(out var disabledEntries, out _, canInvoke: false);
        var disabled = Render(disabledContext);
        disabled.WaitForElement(".state-panel");
        Assert.IsNull(disabledEntries.LastSubmission);

        using var missingContext = CreateContext(out var missingEntries, out _, includeEntry: false);
        var missing = Render(missingContext);
        missing.WaitForElement(".state-panel");
        Assert.IsNull(missingEntries.LastSubmission);
    }

    [TestMethod]
    public void UnauthorizedDiscoveryUsesTheSameClosedUnavailableState()
    {
        using var context = CreateContext(out var entries, out _);
        entries.DiscoveryError = new AgentstrationApiException("Forbidden", "test-correlation", System.Net.HttpStatusCode.Forbidden);

        var rendered = Render(context);

        rendered.WaitForElement(".state-panel");
        Assert.IsNull(entries.LastSubmission);
    }

    [TestMethod]
    public void InitialQueryPrefillsAFormThatRequiresMoreInputWithoutSubmitting()
    {
        const string query = "keep this exact query";
        var form = Entry(true) with
        {
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Form,
                Fields =
                [
                    new() { Name = "request", Type = EntryFieldType.Textarea, Required = true, Role = EntryFieldRole.PrimaryInput },
                    new() { Name = "format", Type = EntryFieldType.Text, Required = true }
                ]
            }
        };
        using var context = CreateContext(out var entries, out _, explicitEntries: [form]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/entry-interactions/{WorkspaceId:D}/tools/assistant?query={Uri.EscapeDataString(query)}");

        var rendered = context.Render<ConsoleEntryInteraction>(parameters => parameters
            .Add(value => value.WorkspaceId, WorkspaceId)
            .Add(value => value.EntryNamespace, "tools")
            .Add(value => value.EntryName, "assistant"));

        rendered.WaitForElement("[data-testid='workplace-form-form']");
        Assert.IsNull(entries.LastSubmission);
        Assert.IsTrue(rendered.Markup.Contains(query, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReplayRestoresMessagesAndFailedTaskState()
    {
        using var context = CreateContext(out _, out var interactions);
        interactions.Interaction = Interaction(InteractionStatus.Failed, TaskId);
        interactions.Messages =
        [
            new(Guid.Parse("66666666-6666-6666-6666-666666666666"), new(WorkspaceId), new(InteractionId), new(TaskId), ConversationRole.User, "diagnose", Now)
        ];
        interactions.CurrentTask = CreateTask(WorkTaskStatus.Failed, new("flow_failed", "The Flow failed.", WorkErrorCategory.Execution, false, Now));

        var rendered = Render(context, InteractionId);

        rendered.WaitForElement("[data-testid='workplace-conversation-message']");
        Assert.AreEqual("diagnose", rendered.Find("[data-testid='workplace-conversation-message'] p").TextContent);
        Assert.AreEqual("Failed", rendered.Find("[data-testid='workplace-inline-task']").GetAttribute("data-task-status"));
        Assert.IsTrue(rendered.Markup.Contains("flow_failed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ActiveTaskCanBeCancelledThroughTheOwningWorkspaceApi()
    {
        using var context = CreateContext(out _, out var interactions);
        interactions.Interaction = Interaction(InteractionStatus.Processing, TaskId);
        interactions.CurrentTask = CreateTask(WorkTaskStatus.Running);

        var rendered = Render(context, InteractionId);
        var cancel = rendered.WaitForElement("[data-testid='console-entry-cancel']");

        await cancel.ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.AreEqual(1, interactions.CancelCalls));
        Assert.AreEqual(WorkspaceId, interactions.CancelWorkspaceId);
        Assert.AreEqual(TaskId, interactions.CancelTaskId);
    }

    private static IRenderedComponent<ConsoleEntryInteraction> Render(BunitContext context, Guid? interactionId = null, string entryName = "assistant") =>
        RenderCore(context, interactionId, entryName);

    private static IRenderedComponent<ConsoleEntryInteraction> RenderCore(BunitContext context, Guid? interactionId, string entryName)
    {
        var url = $"/entry-interactions/{WorkspaceId:D}/tools/{entryName}";
        if (interactionId is not null) url += $"?interaction={interactionId:D}";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return context.Render<ConsoleEntryInteraction>(parameters =>
        {
            parameters.Add(value => value.WorkspaceId, WorkspaceId);
            parameters.Add(value => value.EntryNamespace, "tools");
            parameters.Add(value => value.EntryName, entryName);
        });
    }

    private static BunitContext CreateContext(
        out EntryClient entries,
        out InteractionClient interactions,
        bool canInvoke = true,
        bool includeEntry = true,
        IReadOnlyList<EntryResponse>? explicitEntries = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddAgentstrationWebComponents();
        entries = new(explicitEntries ?? (includeEntry ? [Entry(canInvoke)] : []));
        interactions = new();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(entries);
        context.Services.AddSingleton<IConsoleEntryInteractionApiClient>(interactions);
        context.Services.AddSingleton<IWorkOperationsRealtimeClient>(new RealtimeClient());
        return context;
    }

    private static EntryResponse Entry(bool canInvoke, string name = "assistant") => new(
        WorkspaceId, name, name, WorkResourceTypes.Entries, WorkplaceApiVersions.CoreV1,
        name, "Generic Entry interaction", new EntryPresentation
        {
            Kind = EntryPresentationKind.Prompt,
            Fields = [new() { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }],
            Task = new(EntryTaskDisplay.Visible)
        },
        new EntryExposure { Surfaces = [EntryExposureSurface.Console], WorkplacePlacements = [], Console = new(EntryConsoleRole.Fallback) },
        new("flow", "1.0.0"), new(), 1, Now)
    {
        Namespace = new("tools"),
        Execution = new(canInvoke, canInvoke ? EntryExecutionAvailability.Executable : EntryExecutionAvailability.Disabled, canInvoke ? null : "entry_flow_disabled")
    };

    private static InteractionResponse Interaction(InteractionStatus status, Guid? taskId = null) => new(
        InteractionId, WorkspaceId, "assistant", status, Now, Now, new Dictionary<string, JsonElement>(), [], [], null, taskId, null, 1)
    { EntryNamespace = new("tools") };

    private static WorkTaskResponse CreateTask(WorkTaskStatus status, WorkError? error = null) => new(
        TaskId, WorkspaceId, "assistant", InteractionId, "Generic task", null, status, Now, Now, "run-1", [], [], [], null, error,
        new ShowResultAction("Result", "done"), 1);

    private sealed class EntryClient(IReadOnlyList<EntryResponse> entries) : IEntryAdministrationApiClient
    {
        public (Guid WorkspaceId, ResourceNamespace Namespace, string Name, CreateInteractionRequest Request)? LastSubmission { get; private set; }
        public Exception? DiscoveryError { get; set; }
        public Task<IReadOnlyList<EntryResponse>> GetExposedEntriesAsync(EntryExposureSurface surface, EntryWorkplacePlacement? placement, CancellationToken cancellationToken) =>
            DiscoveryError is null ? Task.FromResult(entries) : Task.FromException<IReadOnlyList<EntryResponse>>(DiscoveryError);
        public Task<EntrySubmissionResponse> SubmitConsoleEntryAsync(Guid workspaceId, ResourceNamespace @namespace, string name, CreateInteractionRequest request, CancellationToken cancellationToken)
        {
            LastSubmission = (workspaceId, @namespace, name, request);
            var interaction = ConsoleEntryInteractionTests.Interaction(InteractionStatus.Completed);
            return Task.FromResult(new EntrySubmissionResponse(interaction, new RespondAction("done"), null));
        }
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

    private sealed class InteractionClient : IConsoleEntryInteractionApiClient
    {
        public InteractionResponse Interaction { get; set; } = ConsoleEntryInteractionTests.Interaction(InteractionStatus.Active);
        public IReadOnlyList<ConversationMessage> Messages { get; set; } = [];
        public WorkTaskResponse CurrentTask { get; set; } = CreateTask(WorkTaskStatus.Completed);
        public int CancelCalls { get; private set; }
        public Guid CancelWorkspaceId { get; private set; }
        public Guid CancelTaskId { get; private set; }
        public Task<InteractionResponse> GetInteractionAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult(Interaction);
        public Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult(Messages);
        public Task<IReadOnlyList<PendingActionContract>> ListPendingActionsAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PendingActionContract>>([]);
        public Task<WorkTaskResponse> GetTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(CurrentTask);
        public Task<WorkTaskResponse> CancelTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            CancelWorkspaceId = workspaceId;
            CancelTaskId = taskId;
            CurrentTask = CurrentTask with { Status = WorkTaskStatus.Cancelled };
            Interaction = Interaction with { Status = InteractionStatus.Cancelled };
            return System.Threading.Tasks.Task.FromResult(CurrentTask);
        }
        public Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskActivity>>([]);
        public Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskResult>>([]);
        public Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkTaskArtifact>>([]);
        public Task<AddConversationMessageResponse> AddMessageAsync(Guid workspaceId, Guid interactionId, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PendingActionResolutionResponse> RespondAsync(Guid workspaceId, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Uri GetArtifactContentUri(Guid workspaceId, Guid taskId, Guid artifactId) => new($"http://localhost/api/workspaces/{workspaceId:D}/tasks/{taskId:D}/artifacts/{artifactId:D}/content");
    }

    private sealed class RealtimeClient : IWorkOperationsRealtimeClient
    {
        public WorkOperationsRealtimeState State => WorkOperationsRealtimeState.Live;
        public event Func<WorkOperationsRealtimeUpdate, Task>? Updated { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public Task StartAsync(IEnumerable<string> workspaceIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyResourceSearchProvider : IResourceSearchProvider
    {
        public Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceSearchResult>>([]);
    }
}
