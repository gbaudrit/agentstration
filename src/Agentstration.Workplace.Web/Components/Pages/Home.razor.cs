using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Workplace.Components;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Workplace.Web.Components.Pages;

public partial class Home
{
    private readonly CancellationTokenSource lifetime = new();
    private IDisposable? realtimeSubscription;
    private WorkplaceWorkspaceResponse? workspace;
    private WorkplaceDashboardResponse? dashboard;
    private IReadOnlyList<WorkplaceDashboardResponse> dashboards = [];
    private EntryResponse? primary;
    private IReadOnlyList<EntryResponse> featuredEntries = [];
    private IReadOnlyList<EntryResponse> standardEntries = [];
    private EntryResponse? selectedHomeEntry;
    private InteractionResponse? interaction;
    private EntryResponse? activeEntry;
    private PendingActionContract? pendingAction;
    private IReadOnlyList<ConversationMessage> messages = [];
    private WorkplaceAction? currentAction;
    private WorkTaskResponse? activeTask;
    private IReadOnlyList<WorkTaskActivity> activities = [];
    private IReadOnlyList<WorkTaskResult> results = [];
    private IReadOnlyList<WorkTaskArtifact> artifacts = [];
    private IReadOnlyList<WorkTaskResponse> tasks = [];
    private bool loading = true;
    private bool busy;
    private int unread;
    private string? loadError;
    private string? interactionError;
    private Guid? loadedInteractionId;
    private string? loadedRoute;
    private bool RealtimeConnected => Realtime.State.ToString() == "Connected";
    private string CurrentWorkspaceName => WorkspaceName ?? workspace?.Name ?? throw new InvalidOperationException("A Workspace route is required.");
    private string CurrentDashboardName => dashboard?.Name ?? DashboardName ?? throw new InvalidOperationException("A Dashboard route is required.");
    private string UserDisplayName => WorkplaceContext.Current?.UserDisplayName is { Length: > 0 } displayName ? displayName : T("You");
    private EntryResponse? SelectedHomeEntry => selectedHomeEntry ?? primary;
    private IEnumerable<EntryResponse> HomeEntries
    {
        get
        {
            if (primary is not null) yield return primary;
            foreach (var entry in featuredEntries) yield return entry;
            foreach (var entry in standardEntries) yield return entry;
        }
    }
    private IEnumerable<EntryResponse> AlternativeEntries => HomeEntries.Where(entry => !SameEntry(entry, SelectedHomeEntry));
    private IEnumerable<EntryResponse> VisibleFeaturedEntries => featuredEntries.Where(entry => !SameEntry(entry, SelectedHomeEntry));
    private IEnumerable<EntryResponse> VisibleStandardEntries => standardEntries.Where(entry => !SameEntry(entry, SelectedHomeEntry));
    [Parameter] public string? WorkspaceName { get; set; }
    [Parameter] public string? DashboardName { get; set; }
    [Parameter] public Guid? ConversationId { get; set; }

    protected override Task OnInitializedAsync() { Realtime.StateChanged += HandleRealtimeStateChanged; return Task.CompletedTask; }

    protected override async Task OnParametersSetAsync()
    {
        var route = $"{WorkspaceName}/{DashboardName}";
        if (!string.Equals(route, loadedRoute, StringComparison.Ordinal))
        {
            loadedRoute = route;
            await LoadAsync();
            return;
        }
        if (workspace is null || loading || ConversationId == loadedInteractionId) return;
        loadedInteractionId = ConversationId;
        if (ConversationId is null)
        {
            ResetInteractionState();
            return;
        }

        try
        {
            interactionError = null;
            await LoadInteractionAsync(ConversationId.Value);
        }
        catch when (!lifetime.IsCancellationRequested)
        {
            loadError = T("ConversationLoadError");
        }
    }

    private async Task LoadAsync()
    {
        var navigationPending = false;
        loading = true; loadError = null;
        try
        {
            var workspaceName = WorkspaceName;
            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                var available = await Api.ListWorkspacesAsync(lifetime.Token);
                workspaceName = available.FirstOrDefault()?.Name
                    ?? throw new InvalidOperationException("No canonical Workspace is available.");
                navigationPending = true;
                Navigation.NavigateTo($"/w/{Uri.EscapeDataString(workspaceName)}", replace: true);
                return;
            }

            workspace = await Api.GetWorkspaceAsync(workspaceName, lifetime.Token);
            WorkplaceContext.SetWorkspace(workspace.Name, workspace.DisplayName, workspace.OrganizationName, workspace.OrganizationDisplayName, workspace.UserDisplayName);
            dashboards = await Api.ListDashboardsAsync(workspaceName, lifetime.Token);
            if (string.IsNullOrWhiteSpace(DashboardName))
            {
                var defaultDashboard = dashboards.SingleOrDefault(value => value.IsDefault)
                    ?? await Api.GetDefaultDashboardAsync(workspaceName, lifetime.Token);
                navigationPending = true;
                Navigation.NavigateTo(DashboardUrl(defaultDashboard.Name), replace: true);
                return;
            }

            dashboard = dashboards.SingleOrDefault(value => string.Equals(value.Name, DashboardName, StringComparison.Ordinal))
                ?? await Api.GetDashboardAsync(workspaceName, DashboardName, lifetime.Token);
            var reference = dashboard.Entries.FirstOrDefault(value => value.Role == DashboardItemRole.Primary);
            primary = reference is null ? null : await Api.GetEntryAsync(EntryId(reference), lifetime.Token);
            var featured = dashboard.Entries.Where(value => value.Role == DashboardItemRole.Featured).OrderBy(value => value.Order);
            featuredEntries = await Task.WhenAll(featured.Select(value => Api.GetEntryAsync(EntryId(value), lifetime.Token)));
            var standard = dashboard.Entries.Where(value => value.Role == DashboardItemRole.Standard).OrderBy(value => value.Order);
            standardEntries = await Task.WhenAll(standard.Select(value => Api.GetEntryAsync(EntryId(value), lifetime.Token)));
            selectedHomeEntry = primary ?? featuredEntries.FirstOrDefault() ?? standardEntries.FirstOrDefault();
            await RefreshOverviewAsync();
            if (ConversationId is not null) await LoadInteractionAsync(ConversationId.Value);
            loadedInteractionId = ConversationId;
            realtimeSubscription ??= Realtime.OnWorkspaceChanged(HandleRealtimeEvent);
            try { await Realtime.StartAsync(workspace.Id.ToString("D"), 0, lifetime.Token); } catch when (!lifetime.IsCancellationRequested) { }
        }
        catch when (!lifetime.IsCancellationRequested) { loadError = T("WorkplaceLoadError"); }
        finally { if (!navigationPending) loading = false; }
    }

    private async Task SubmitAsync(EntryResponse entry, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values)
    {
        if (busy) return;
        busy = true; interactionError = null;
        try
        {
            var submission = await Api.SubmitAsync(CurrentWorkspaceName, new EntryId(entry.Id, entry.Namespace), values, lifetime.Token);
            activeEntry = entry; interaction = submission.Interaction; messages = submission.Interaction.Messages; currentAction = submission.Action; activeTask = submission.Task;
            pendingAction = await CurrentPendingActionAsync(submission.Interaction);
            if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id);
            await RefreshOverviewAsync();
            Navigation.NavigateTo(InteractionUrl(submission.Interaction.Id));
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = T("RequestSendError"); }
        finally { busy = false; }
    }

    private async Task RespondAsync(PendingActionAnswer answer)
    {
        if (interaction is null || busy) return;
        busy = true; interactionError = null;
        try
        {
            var resolution = await Api.RespondAsync(CurrentWorkspaceName, interaction.Id, answer.PendingActionId.Value, answer.ResumeToken, answer.Values, lifetime.Token);
            interaction = resolution.Interaction; messages = resolution.Interaction.Messages; currentAction = resolution.NextAction; activeTask = resolution.Task;
            pendingAction = await CurrentPendingActionAsync(resolution.Interaction);
            if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id);
            await RefreshOverviewAsync();
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = T("ResponseApplyError"); }
        finally { busy = false; }
    }

    private async Task ContinueAsync(string content)
    {
        if (interaction is null || busy) return;
        busy = true; interactionError = null;
        try
        {
            var continuation = await Api.AddMessageAsync(CurrentWorkspaceName, interaction.Id, content, lifetime.Token);
            interaction = continuation.Interaction; currentAction = continuation.Action; activeTask = continuation.Task;
            messages = await Api.ListMessagesAsync(CurrentWorkspaceName, interaction.Id, lifetime.Token);
            if (interaction.TaskId is not null) await RefreshActiveTaskAsync(interaction.TaskId.Value);
            await RefreshOverviewAsync();
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = T("FollowUpSendError"); }
        finally { busy = false; }
    }

    private Task NewRequest()
    {
        ResetInteractionState();
        loadedInteractionId = null;
        Navigation.NavigateTo(DashboardUrl(CurrentDashboardName), replace: true);
        return Task.CompletedTask;
    }

    private void HandleRealtimeEvent(WorkplaceEventContract workplaceEvent) => _ = InvokeAsync(async () =>
    {
        if (lifetime.IsCancellationRequested) return;
        switch (workplaceEvent)
        {
            case MessageAddedEvent value when value.Message.InteractionId.Value == interaction?.Id: messages = await Api.ListMessagesAsync(CurrentWorkspaceName, value.Message.InteractionId.Value, lifetime.Token); break;
            case InteractionUpdatedEvent value when value.InteractionId == interaction?.Id:
                await LoadInteractionAsync(value.InteractionId); break;
            case PendingActionCreatedEvent value when value.PendingAction.InteractionId is Guid interactionId && interactionId == interaction?.Id:
                await LoadInteractionAsync(interactionId); break;
            case PendingActionResolvedEvent when interaction is not null:
                await LoadInteractionAsync(interaction.Id); break;
            case TaskCreatedEvent: await RefreshOverviewAsync(); break;
            case TaskStatusChangedEvent status when status.TaskId == activeTask?.Id:
                await RefreshActiveTaskAsync(activeTask!.Id); await RefreshOverviewAsync(); break;
            case TaskActivityAddedEvent activity when activity.Activity.WorkTaskId.Value == activeTask?.Id:
                await RefreshActiveTaskAsync(activeTask!.Id); await RefreshOverviewAsync(); break;
            case TaskResultAddedEvent result when result.Result.WorkTaskId.Value == activeTask?.Id:
                await RefreshActiveTaskAsync(activeTask!.Id); await RefreshOverviewAsync(); break;
            case TaskArtifactAddedEvent artifact when artifact.Artifact.WorkTaskId == activeTask?.Id:
                await RefreshActiveTaskAsync(activeTask!.Id); await RefreshOverviewAsync(); break;
            case FlowRunStartedEvent started when started.InteractionId == interaction?.Id:
                if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id); break;
            case FlowRunCompletedEvent completed when completed.InteractionId == interaction?.Id:
                if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id); break;
            case NotificationCreatedEvent or NotificationUpdatedEvent or UnreadNotificationCountChangedEvent: await RefreshUnreadCountAsync(); break;
        }
        StateHasChanged();
    });

    private void HandleRealtimeStateChanged(Microsoft.AspNetCore.SignalR.Client.HubConnectionState state) => _ = InvokeAsync(async () =>
    {
        if (state == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            try
            {
                await RefreshOverviewAsync();
                if (interaction is not null) await LoadInteractionAsync(interaction.Id);
                else if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id);
                interactionError = null;
            }
            catch when (!lifetime.IsCancellationRequested)
            {
                interactionError = T("ConversationRestoreError");
            }
        }
        StateHasChanged();
    });

    private async Task RefreshActiveTaskAsync(Guid taskId) { activeTask = await Api.GetTaskAsync(CurrentWorkspaceName, taskId, lifetime.Token); activities = await Api.ListActivitiesAsync(CurrentWorkspaceName, taskId, lifetime.Token); results = await Api.ListResultsAsync(CurrentWorkspaceName, taskId, lifetime.Token); artifacts = await Api.ListArtifactsAsync(CurrentWorkspaceName, taskId, lifetime.Token); }
    private async Task RefreshOverviewAsync() { tasks = await Api.ListTasksAsync(CurrentWorkspaceName, null, lifetime.Token); await RefreshUnreadCountAsync(); }
    private async Task LoadInteractionAsync(Guid interactionId)
    {
        interaction = await Api.GetInteractionAsync(CurrentWorkspaceName, interactionId, lifetime.Token);
        activeEntry = await Api.GetEntryAsync(new EntryId(interaction.EntryId, interaction.EntryNamespace), lifetime.Token);
        messages = await Api.ListMessagesAsync(CurrentWorkspaceName, interactionId, lifetime.Token);
        pendingAction = await CurrentPendingActionAsync(interaction);
        currentAction = interaction.ImmediateResult;
        if (interaction.TaskId is not null) await RefreshActiveTaskAsync(interaction.TaskId.Value);
        else { activeTask = null; activities = []; results = []; artifacts = []; }
    }
    private void ResetInteractionState() { interaction = null; activeEntry = null; pendingAction = null; messages = []; currentAction = null; activeTask = null; activities = []; results = []; artifacts = []; interactionError = null; }
    private async Task<PendingActionContract?> CurrentPendingActionAsync(InteractionResponse value)
    {
        if (value.PendingActionId is null) return null;
        return (await Api.ListPendingActionsAsync(CurrentWorkspaceName, value.Id, lifetime.Token))
            .SingleOrDefault(action => action.Id == value.PendingActionId && action.Status == PendingActionStatus.Pending);
    }
    private async Task RefreshUnreadCountAsync() { unread = await Api.GetUnreadCountAsync(CurrentWorkspaceName, lifetime.Token); }
    private string ArtifactUrl(WorkTaskArtifact artifact) => Api.GetArtifactContentUri(CurrentWorkspaceName, artifact.WorkTaskId.Value, artifact.Id.Value).ToString();
    private string TaskUrl(WorkTaskResponse task) => $"/w/{Uri.EscapeDataString(CurrentWorkspaceName)}/tasks/{task.Id}";
    private string DashboardUrl(string dashboardName) => $"/w/{Uri.EscapeDataString(CurrentWorkspaceName)}/d/{Uri.EscapeDataString(dashboardName)}";
    private string InteractionUrl(Guid interactionId) => $"/w/{Uri.EscapeDataString(CurrentWorkspaceName)}/d/{Uri.EscapeDataString(CurrentDashboardName)}/conversations/{interactionId}";
    private void DashboardChanged(ChangeEventArgs args) { var name = args.Value?.ToString(); if (!string.IsNullOrWhiteSpace(name)) Navigation.NavigateTo(DashboardUrl(name)); }
    private void SelectHomeEntry(EntryResponse entry) => selectedHomeEntry = entry;
    private DashboardItemRole EntryRole(EntryResponse entry)
    {
        if (SameEntry(entry, primary)) return DashboardItemRole.Primary;
        if (featuredEntries.Any(value => SameEntry(value, entry))) return DashboardItemRole.Featured;
        return DashboardItemRole.Standard;
    }
    private static bool SameEntry(EntryResponse? left, EntryResponse? right) => left is not null && right is not null && left.Id == right.Id && left.Namespace == right.Namespace;
    private static string DashboardIcon(WorkplaceDashboardResponse value)
    {
        if (!string.IsNullOrWhiteSpace(value.Icon)) return value.Icon;
        var key = $"{value.Name} {value.DisplayName}".ToLowerInvariant();
        if (key.Contains("home", StringComparison.Ordinal) || key.Contains("maison", StringComparison.Ordinal)) return "home";
        if (key.Contains("travel", StringComparison.Ordinal) || key.Contains("voyage", StringComparison.Ordinal) || key.Contains("trip", StringComparison.Ordinal)) return "plane";
        if (key.Contains("cook", StringComparison.Ordinal) || key.Contains("kitchen", StringComparison.Ordinal) || key.Contains("cuisine", StringComparison.Ordinal)) return "chef-hat";
        if (key.Contains("document", StringComparison.Ordinal) || key.Contains("file", StringComparison.Ordinal)) return "folder";
        if (key.Contains("shop", StringComparison.Ordinal) || key.Contains("achat", StringComparison.Ordinal)) return "shopping-bag";
        return "layout-grid";
    }
    private static EntryId EntryId(DashboardEntryReferenceResponse reference) => new(reference.EntryResourceId, reference.Namespace);
    private string T(string key, params object[] arguments) => Localizer[key, arguments];
    private static EntryResource ToDefinition(EntryResponse value) => new() { WorkspaceId = new(value.WorkspaceId), Id = new(value.Id, value.Namespace), Name = value.Name, DisplayName = value.DisplayName, Description = value.Description, Presentation = value.Presentation, ResolvedTarget = value.ResolvedTarget, Behavior = value.Behavior, ApiVersion = value.ApiVersion, Type = value.Type, Version = value.Version, PublishedAt = value.PublishedAt };
    public void Dispose() { Realtime.StateChanged -= HandleRealtimeStateChanged; realtimeSubscription?.Dispose(); lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this); }
}
