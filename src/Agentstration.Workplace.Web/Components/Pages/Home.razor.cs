using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Workplace.Components;

namespace Agentstration.Workplace.Web.Components.Pages;

public partial class Home
{
    private const string WorkspaceName = "personal";
    private readonly CancellationTokenSource lifetime = new();
    private IDisposable? realtimeSubscription;
    private WorkplaceWorkspaceResponse? workspace;
    private EntryResponse? primary;
    private IReadOnlyList<EntryResponse> standardEntries = [];
    private InteractionResponse? interaction;
    private IReadOnlyList<ConversationMessage> messages = [];
    private WorkplaceAction? currentAction;
    private WorkTaskResponse? activeTask;
    private IReadOnlyList<WorkTaskActivity> activities = [];
    private IReadOnlyList<WorkTaskResult> results = [];
    private IReadOnlyList<WorkTaskArtifact> artifacts = [];
    private IReadOnlyList<WorkTaskResponse> tasks = [];
    private IReadOnlyList<InteractionResponse> recentInteractions = [];
    private IReadOnlyList<WorkNotification> notifications = [];
    private bool loading = true;
    private bool busy;
    private bool showMoreEntries;
    private int unread;
    private string? loadError;
    private string? interactionError;
    private Guid? loadedInteractionId;
    private bool RealtimeConnected => Realtime.State.ToString() == "Connected";
    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "interaction")] public Guid? RequestedInteractionId { get; set; }

    protected override Task OnInitializedAsync() { Realtime.StateChanged += HandleRealtimeStateChanged; return LoadAsync(); }

    protected override async Task OnParametersSetAsync()
    {
        if (workspace is null || loading || RequestedInteractionId == loadedInteractionId) return;
        loadedInteractionId = RequestedInteractionId;
        if (RequestedInteractionId is null)
        {
            ResetInteractionState();
            return;
        }

        try
        {
            interactionError = null;
            await LoadInteractionAsync(RequestedInteractionId.Value);
        }
        catch when (!lifetime.IsCancellationRequested)
        {
            loadError = "The requested conversation could not be loaded from the local Work API.";
        }
    }

    private async Task LoadAsync()
    {
        loading = true; loadError = null;
        try
        {
            workspace = await Api.GetWorkspaceAsync(WorkspaceName, lifetime.Token);
            var reference = workspace.Entries.FirstOrDefault(value => value.Role == WorkspaceEntryRole.Primary);
            primary = reference is null ? null : await Api.GetEntryAsync(ResourceName(reference.EntryResourceId), lifetime.Token);
            var standard = workspace.Entries.Where(value => value.Role == WorkspaceEntryRole.Standard).OrderBy(value => value.Order);
            standardEntries = await Task.WhenAll(standard.Select(value => Api.GetEntryAsync(ResourceName(value.EntryResourceId), lifetime.Token)));
            await RefreshOverviewAsync();
            if (RequestedInteractionId is not null) await LoadInteractionAsync(RequestedInteractionId.Value);
            loadedInteractionId = RequestedInteractionId;
            realtimeSubscription ??= Realtime.OnWorkspaceChanged(HandleRealtimeEvent);
            try { await Realtime.StartAsync(workspace.Id, 0, lifetime.Token); } catch when (!lifetime.IsCancellationRequested) { }
        }
        catch when (!lifetime.IsCancellationRequested) { loadError = "The local Work API could not be reached. Check that it is running, then retry."; }
        finally { loading = false; }
    }

    private async Task SubmitAsync(EntryResponse entry, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values)
    {
        if (busy) return;
        busy = true; interactionError = null;
        try
        {
            var submission = await Api.SubmitAsync(WorkspaceName, entry.Name, values, lifetime.Token);
            interaction = submission.Interaction; messages = submission.Interaction.Messages; currentAction = submission.Action; activeTask = submission.Task;
            if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id);
            await RefreshOverviewAsync();
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = "Your request could not be sent. Please try again."; }
        finally { busy = false; }
    }

    private async Task RespondAsync(PendingActionAnswer answer)
    {
        if (interaction is null || busy) return;
        busy = true; interactionError = null;
        try
        {
            var resolution = await Api.RespondAsync(WorkspaceName, interaction.Id, answer.PendingActionId.Value, answer.ResumeToken, answer.Values, lifetime.Token);
            interaction = resolution.Interaction; messages = resolution.Interaction.Messages; currentAction = resolution.NextAction; activeTask = resolution.Task;
            if (activeTask is not null) await RefreshActiveTaskAsync(activeTask.Id);
            await RefreshOverviewAsync();
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = "That response could not be applied. It may have expired; start a new request if retrying does not work."; }
        finally { busy = false; }
    }

    private async Task ContinueAsync(string content)
    {
        if (interaction is null || busy) return;
        busy = true; interactionError = null;
        try
        {
            var continuation = await Api.AddMessageAsync(WorkspaceName, interaction.Id, content, lifetime.Token);
            interaction = continuation.Interaction; currentAction = continuation.Action; activeTask = continuation.Task;
            messages = await Api.ListMessagesAsync(WorkspaceName, interaction.Id, lifetime.Token);
            if (interaction.TaskId is not null) await RefreshActiveTaskAsync(interaction.TaskId.Value);
            await RefreshOverviewAsync();
        }
        catch when (!lifetime.IsCancellationRequested) { interactionError = "Your follow-up could not be sent. Please try again."; }
        finally { busy = false; }
    }

    private Task NewRequest()
    {
        ResetInteractionState();
        loadedInteractionId = null;
        Navigation.NavigateTo("/", replace: true);
        return Task.CompletedTask;
    }

    private void HandleRealtimeEvent(WorkplaceEventContract workplaceEvent) => _ = InvokeAsync(async () =>
    {
        if (lifetime.IsCancellationRequested) return;
        switch (workplaceEvent)
        {
            case MessageAddedEvent value when value.Message.InteractionId.Value == interaction?.Id: messages = await Api.ListMessagesAsync(WorkspaceName, value.Message.InteractionId.Value, lifetime.Token); break;
            case InteractionUpdatedEvent value when value.InteractionId == interaction?.Id:
                interaction = await Api.GetInteractionAsync(WorkspaceName, value.InteractionId, lifetime.Token); break;
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
            case NotificationCreatedEvent or NotificationUpdatedEvent or UnreadNotificationCountChangedEvent: await RefreshNotificationsAsync(); break;
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
                interactionError = "The latest conversation state could not be restored. Please try again.";
            }
        }
        StateHasChanged();
    });

    private async Task RefreshActiveTaskAsync(Guid taskId) { activeTask = await Api.GetTaskAsync(WorkspaceName, taskId, lifetime.Token); activities = await Api.ListActivitiesAsync(WorkspaceName, taskId, lifetime.Token); results = await Api.ListResultsAsync(WorkspaceName, taskId, lifetime.Token); artifacts = await Api.ListArtifactsAsync(WorkspaceName, taskId, lifetime.Token); }
    private async Task RefreshOverviewAsync() { tasks = await Api.ListTasksAsync(WorkspaceName, null, lifetime.Token); recentInteractions = await Api.ListInteractionsAsync(WorkspaceName, 20, lifetime.Token); await RefreshNotificationsAsync(); }
    private async Task LoadInteractionAsync(Guid interactionId)
    {
        interaction = await Api.GetInteractionAsync(WorkspaceName, interactionId, lifetime.Token);
        messages = await Api.ListMessagesAsync(WorkspaceName, interactionId, lifetime.Token);
        currentAction = interaction.ImmediateResult;
        if (interaction.TaskId is not null) await RefreshActiveTaskAsync(interaction.TaskId.Value);
        else { activeTask = null; activities = []; results = []; artifacts = []; }
    }
    private void ResetInteractionState() { interaction = null; messages = []; currentAction = null; activeTask = null; activities = []; results = []; artifacts = []; interactionError = null; }
    private async Task RefreshNotificationsAsync() { notifications = await Api.ListNotificationsAsync(WorkspaceName, false, lifetime.Token); unread = await Api.GetUnreadCountAsync(WorkspaceName, lifetime.Token); }
    private async Task MarkReadAsync(WorkNotificationId id) { await Api.MarkNotificationReadAsync(WorkspaceName, id.Value, lifetime.Token); await RefreshNotificationsAsync(); }
    private async Task MarkAllReadAsync() { await Api.MarkAllNotificationsReadAsync(WorkspaceName, lifetime.Token); await RefreshNotificationsAsync(); }
    private string ArtifactUrl(WorkTaskArtifact artifact) => Api.GetArtifactContentUri(WorkspaceName, artifact.WorkTaskId.Value, artifact.Id.Value).ToString();
    private static string ResourceName(string resourceId) => resourceId[(resourceId.LastIndexOf('/') + 1)..];
    private static string ConversationTitle(InteractionResponse value) => value.Messages.FirstOrDefault(message => message.Role == ConversationRole.User)?.Content ?? "Conversation";
    private static string ConversationStatus(InteractionStatus value) => value switch { InteractionStatus.Idle => "Ready to continue", InteractionStatus.Processing => "In progress", InteractionStatus.WaitingForUser => "Needs input", InteractionStatus.Closed => "Closed", _ => value.ToString() };
    private static EntryResource ToDefinition(EntryResponse value) => new() { Id = new(value.Id), Name = value.Name, DisplayName = value.DisplayName, Description = value.Description, Presentation = value.Presentation, ResolvedTarget = value.ResolvedTarget, Behavior = value.Behavior, ApiVersion = value.ApiVersion, Type = value.Type, Version = value.Version, PublishedAt = value.PublishedAt };
    public void Dispose() { Realtime.StateChanged -= HandleRealtimeStateChanged; realtimeSubscription?.Dispose(); lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this); }
}
