using System.Globalization;
using System.Net;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.WorkOperations;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components.Pages;

public partial class ConsoleEntryInteraction
{
    [Parameter] public Guid WorkspaceId { get; set; }
    [Parameter] public string EntryNamespace { get; set; } = ResourceNamespace.DefaultValue;
    [Parameter] public string EntryName { get; set; } = string.Empty;
    [Parameter, SupplyParameterFromQuery(Name = "query")] public string? InitialQuery { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "interaction")] public Guid? InteractionId { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? ContextValue { get; set; }
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private EntryResponse? entry, newerEntry;
    private InteractionResponse? interaction;
    private InteractionBundle? newerInteraction;
    private PendingActionContract? pendingAction;
    private WorkplaceAction? currentAction;
    private WorkTaskResponse? activeTask;
    private IReadOnlyList<ConversationMessage> messages = [];
    private IReadOnlyList<WorkTaskActivity> activities = [];
    private IReadOnlyList<WorkTaskResult> results = [];
    private IReadOnlyList<WorkTaskArtifact> artifacts = [];
    private IReadOnlyDictionary<string, JsonElement> initialValues = new Dictionary<string, JsonElement>();
    private Agentstration.Workplace.Components.InteractionView? interactionView;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? loadedKey, error, attemptedQuery;
    private string section = "interaction";
    private bool loading, busy, canInvoke, requestDraft, conversationDraft, messagesKnown, subscribed, disposed, realtimeStarted;
    private long loadVersion, readVersion, initialEditorVersion, conversationEditorVersion, responseAcknowledgement;
    public ConsoleEntryInteraction() => lifetimeToken = lifetime.Token;
    private string ContextKey => $"{WorkspaceId:D}|{EntryNamespace}|{EntryName}|{InteractionId}|{InitialQuery}|{ContextValue}|{ContextState.Current?.UserId}|{ContextState.Current?.SelectedScopeRef}";
    private bool HasDraft => !disposed && (requestDraft || conversationDraft);
    private bool CanSubmit => !disposed && !busy && !loading && canInvoke && newerEntry is null && newerInteraction is null;
    private bool CanCancel => !disposed && activeTask?.Status is WorkTaskStatus.Pending or WorkTaskStatus.Running or WorkTaskStatus.ActionRequired or WorkTaskStatus.Paused;
    private string AvailabilityMessage => entry?.Execution?.Availability == EntryExecutionAvailability.Disabled ? T("DisabledMessage") : T("UnavailableTargetMessage");
    private string? ExecutionWorkspaceName => ContextState.Current?.Workspaces.FirstOrDefault(value => value.Id == WorkspaceId)?.DisplayName;
    private IReadOnlyList<ResourceNavigationItem> Sections => [new("overview", Shared["Overview"], "home"), new("interaction", T("EntryInteraction"), "messages")];
    private void SelectSection(string? value) => section = value ?? "interaction";
    private string T(string key) => Localizer[key].Value;
    private sealed record Setting(string Name, string Value);
    private IReadOnlyList<Setting> SavedSettings => entry is null ? [] : [new(T("PinnedFlow"), $"{entry.ResolvedTarget.ScopeRef?.Value ?? "—"} · {entry.ResolvedTarget.Namespace.Value}/{entry.ResolvedTarget.FlowResourceId}"), new(Shared["Version"], entry.ResolvedTarget.Version), new(T("InputFields"), entry.Presentation.Fields.Count.ToString(CultureInfo.CurrentCulture)), new(T("AllowConversation"), T(entry.Behavior.AllowConversation ? "Yes" : "No")), new(T("StreamResponse"), T(entry.Behavior.StreamResponse ? "Yes" : "No"))];
    private void RequestDraftChanged(string? key, bool value) { if (!disposed && key == loadedKey && entry is not null) requestDraft = value; }
    private void ConversationDraftChanged(string? key, bool value) { if (!disposed && key == loadedKey && entry is not null) conversationDraft = value; }
    private bool Current(long revision, string? key) => !disposed && revision == loadVersion && key == loadedKey && key == ContextKey;
    private bool CurrentRead(long revision, string? key, long read) => Current(revision, key) && read == readVersion;
    private static ResourceLoadStatus FailureStatus(Exception exception) => exception is AgentstrationApiException api ? api.StatusCode switch { HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden, HttpStatusCode.NotFound => ResourceLoadStatus.NotFound, _ => ResourceLoadStatus.Failed } : ResourceLoadStatus.Failed;
    private AgentstrationApiException IdentityMismatch() => new(T("IdentityMismatch"), Guid.NewGuid().ToString("N"), HttpStatusCode.NotFound);
    protected override async Task OnParametersSetAsync()
    {
        await ContextState.EnsureLoadedAsync(lifetimeToken); if (disposed) return;
        if (!subscribed) { subscribed = true; ContextState.Changed += ContextChanged; Realtime.Updated += OnRealtimeAsync; }
        if (loadedKey == ContextKey) return;
        loadedKey = ContextKey; ++loadVersion; ++readVersion; ClearPrivate();
        if (InitialQuery is null) attemptedQuery = null;
        await LoadAsync(autoSubmit: true);
    }
    private void ContextChanged() => _ = InvokeAsync(async () => { if (disposed || loadedKey == ContextKey) return; loadedKey = ContextKey; ++loadVersion; ++readVersion; ClearPrivate(); await LoadAsync(autoSubmit: false); StateHasChanged(); });
    private void ClearPrivate()
    {
        entry = newerEntry = null; interaction = null; newerInteraction = null; pendingAction = null; currentAction = null; activeTask = null;
        messages = []; activities = []; results = []; artifacts = []; initialValues = new Dictionary<string, JsonElement>();
        requestDraft = conversationDraft = messagesKnown = canInvoke = busy = false; error = null; interactionView = null;
        ++initialEditorVersion; ++conversationEditorVersion; ++responseAcknowledgement;
    }
    private Task RefreshAsync() => busy || loading ? Task.CompletedTask : LoadAsync(autoSubmit: false);
    private async Task LoadAsync(bool autoSubmit)
    {
        var revision = loadVersion; var key = loadedKey; var read = ++readVersion; loading = true;
        loadState = new(entry is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = entry is not null }; error = null;
        try
        {
            var ns = ResourceNamespace.Parse(EntryNamespace); var workspace = WorkspaceId; var name = EntryName;
            var available = await Entries.GetConsoleEntriesAsync(lifetimeToken); if (!CurrentRead(revision, key, read)) return;
            var incoming = available.SingleOrDefault(value => value.WorkspaceId == workspace && value.Namespace == ns && value.Name == name);
            if (incoming is null) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound) { Message = T("UnavailableMessage") }; return; }
            if (entry is not null && entry.Id != incoming.Id) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound) { Message = T("UnavailableMessage") }; return; }
            canInvoke = incoming.Execution?.CanInvoke == true;
            if (entry is not null && HasDraft && DefinitionFingerprint(entry) != DefinitionFingerprint(incoming)) newerEntry = incoming;
            else { entry = incoming; newerEntry = null; }
            if (!realtimeStarted) { await Realtime.StartAsync([workspace.ToString("D")], lifetimeToken); if (!CurrentRead(revision, key, read)) return; realtimeStarted = true; }
            var id = interaction?.Id ?? InteractionId;
            if (id is { } interactionId)
            {
                var bundle = await ReadInteractionAsync(workspace, interactionId, entry!, revision, key, read); if (bundle is null) return; AdoptOrBuffer(bundle);
            }
            else if (initialValues.Count == 0 && !requestDraft) { initialValues = InitialValues(entry!, InitialQuery); requestDraft = initialValues.Count > 0; }
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
            var queryKey = $"{workspace:D}|{ns.Value}|{name}|{InitialQuery}";
            if (autoSubmit && interaction is null && InitialQuery is not null && canInvoke && CanSubmitInitialQuery(entry!) && attemptedQuery != queryKey)
            {
                attemptedQuery = queryKey; loading = false;
                await SubmitCoreAsync(initialValues);
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException or ArgumentException or InvalidOperationException) { if (CurrentRead(revision, key, read)) ReadFailed(exception); }
        finally { if (Current(revision, key)) loading = false; }
    }
    private static string DefinitionFingerprint(EntryResponse value) => JsonSerializer.Serialize(new { value.Id, value.Version, value.Presentation, value.ResolvedTarget, value.Behavior });
    private void ValidateInteraction(InteractionResponse value, Guid workspace, EntryResponse definition, Guid? expectedId = null)
    {
        if (value.Id == Guid.Empty || value.WorkspaceId != workspace || value.EntryId != definition.Name || value.EntryNamespace != definition.Namespace || expectedId is { } id && value.Id != id) throw IdentityMismatch();
        if (value.Messages.Any(message => message.WorkspaceId.Value != workspace || message.InteractionId.Value != value.Id)) throw IdentityMismatch();
    }
    private void ValidateTask(WorkTaskResponse value, Guid workspace, Guid expectedId, Guid interactionId)
    {
        if (value.Id != expectedId || value.WorkspaceId != workspace || value.InteractionId is { } owner && owner != interactionId) throw IdentityMismatch();
    }
    private async Task<InteractionBundle?> ReadInteractionAsync(Guid workspace, Guid id, EntryResponse definition, long revision, string? key, long read)
    {
        var loaded = await Interactions.GetInteractionAsync(workspace, id, lifetimeToken); if (!CurrentRead(revision, key, read)) return null; ValidateInteraction(loaded, workspace, definition, id);
        var loadedMessages = await Interactions.ListMessagesAsync(workspace, id, lifetimeToken); if (!CurrentRead(revision, key, read)) return null;
        if (loadedMessages.Any(value => value.WorkspaceId.Value != workspace || value.InteractionId.Value != id)) throw IdentityMismatch();
        PendingActionContract? pending = null;
        if (loaded.PendingActionId is { } actionId)
        {
            var actions = await Interactions.ListPendingActionsAsync(workspace, id, lifetimeToken); if (!CurrentRead(revision, key, read)) return null;
            pending = actions.SingleOrDefault(value => value.Id == actionId && value.Status == PendingActionStatus.Pending);
            if (pending is not null && (pending.WorkspaceId != workspace || pending.InteractionId is { } owner && owner != id)) throw IdentityMismatch();
        }
        WorkTaskResponse? task = null; IReadOnlyList<WorkTaskActivity> loadedActivities = []; IReadOnlyList<WorkTaskResult> loadedResults = []; IReadOnlyList<WorkTaskArtifact> loadedArtifacts = [];
        if (loaded.TaskId is { } taskId)
        {
            task = await Interactions.GetTaskAsync(workspace, taskId, lifetimeToken); if (!CurrentRead(revision, key, read)) return null; ValidateTask(task, workspace, taskId, id);
            loadedActivities = await Interactions.ListActivitiesAsync(workspace, taskId, lifetimeToken); if (!CurrentRead(revision, key, read)) return null;
            loadedResults = await Interactions.ListResultsAsync(workspace, taskId, lifetimeToken); if (!CurrentRead(revision, key, read)) return null;
            loadedArtifacts = await Interactions.ListArtifactsAsync(workspace, taskId, lifetimeToken); if (!CurrentRead(revision, key, read)) return null;
            if (loadedActivities.Any(value => value.WorkspaceId.Value != workspace || value.WorkTaskId.Value != taskId) || loadedResults.Any(value => value.WorkspaceId.Value != workspace || value.WorkTaskId.Value != taskId) || loadedArtifacts.Any(value => value.WorkspaceId.Value != workspace || value.WorkTaskId.Value != taskId)) throw IdentityMismatch();
        }
        return new(loaded, loadedMessages, pending, pending is null ? loaded.ImmediateResult : TaskPendingActionUi.ToAction(pending), task, loadedActivities, loadedResults, loadedArtifacts);
    }
    private void AdoptOrBuffer(InteractionBundle bundle)
    {
        if (interactionView?.HasPendingDraft == true && pendingAction?.Id != bundle.Pending?.Id) { newerInteraction = bundle; return; }
        Adopt(bundle);
    }
    private void Adopt(InteractionBundle bundle)
    {
        interaction = bundle.Interaction; messages = bundle.Messages; pendingAction = bundle.Pending; currentAction = bundle.Action; activeTask = bundle.Task;
        activities = bundle.Activities; results = bundle.Results; artifacts = bundle.Artifacts; messagesKnown = true; newerInteraction = null;
    }
    private void ReadFailed(Exception exception)
    {
        var status = FailureStatus(exception); if (exception is ArgumentException or InvalidOperationException) status = ResourceLoadStatus.NotFound;
        if (status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearPrivate();
        loadState = new(status) { HasContent = entry is not null, Message = status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound ? T("UnavailableMessage") : T("LoadFailed"), ErrorId = (exception as AgentstrationApiException)?.ErrorId };
    }
    private Task SubmitAsync(IReadOnlyDictionary<string, JsonElement> values) => SubmitCoreAsync(values);
    private async Task SubmitCoreAsync(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (entry is not { } definition || !CanSubmit) return; var revision = loadVersion; var key = loadedKey; var workspace = WorkspaceId; busy = true; ++readVersion; error = null;
        try
        {
            var submission = await Entries.SubmitConsoleEntryAsync(workspace, definition.Namespace, definition.Name, new(values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal)), lifetimeToken); if (!Current(revision, key)) return;
            ValidateInteraction(submission.Interaction, workspace, definition);
            if (submission.Task is { } task) ValidateTask(task, workspace, submission.Interaction.TaskId ?? task.Id, submission.Interaction.Id);
            interaction = submission.Interaction; messages = submission.Interaction.Messages; currentAction = submission.Action; activeTask = submission.Task; messagesKnown = true; requestDraft = false;
            if (activeTask is not null) await RefreshInteractionAsync(); if (!Current(revision, key) || entry is null) return;
            Navigation.NavigateTo($"{ConsoleEntryInteractionNavigation.Build(workspace, definition.Namespace, definition.Name)}?interaction={submission.Interaction.Id:D}", replace: true);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(revision, key)) error = T("SubmitFailed"); }
        finally { if (Current(revision, key)) busy = false; }
    }
    private async Task<bool> ContinueAsync(string content)
    {
        if (interaction is not { } current || entry is not { } definition || !CanSubmit) return false;
        var revision = loadVersion; var key = loadedKey; var workspace = WorkspaceId; busy = true; ++readVersion; error = null; var accepted = false;
        try
        {
            var result = await Interactions.AddMessageAsync(workspace, current.Id, content, lifetimeToken); if (!Current(revision, key)) return false;
            ValidateInteraction(result.Interaction, workspace, definition, current.Id); if (result.Task is { } task) ValidateTask(task, workspace, result.Interaction.TaskId ?? task.Id, current.Id);
            accepted = true; ApplyContinuation(result.Interaction, result.Action, result.Task); await RefreshInteractionAsync();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(revision, key)) error = T("ContinueFailed"); }
        finally { if (Current(revision, key)) busy = false; }
        return accepted;
    }
    private async Task RespondAsync(Agentstration.Workplace.Components.PendingActionAnswer answer)
    {
        if (interaction is not { } current || entry is not { } definition || !CanSubmit || pendingAction?.Id != answer.PendingActionId.Value) return;
        var revision = loadVersion; var key = loadedKey; var workspace = WorkspaceId; busy = true; ++readVersion; error = null;
        try
        {
            var result = await Interactions.RespondAsync(workspace, current.Id, answer.PendingActionId.Value, answer.ResumeToken, answer.Values, lifetimeToken); if (!Current(revision, key)) return;
            ValidateInteraction(result.Interaction, workspace, definition, current.Id); if (result.Task is { } task) ValidateTask(task, workspace, result.Interaction.TaskId ?? task.Id, current.Id);
            ++responseAcknowledgement; ApplyContinuation(result.Interaction, result.NextAction, result.Task); await RefreshInteractionAsync();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(revision, key)) error = T("ResponseFailed"); }
        finally { if (Current(revision, key)) busy = false; }
    }
    private async Task CancelAsync()
    {
        if (activeTask is not { } current || interaction is not { } currentInteraction || !CanCancel || busy || loading) return;
        var revision = loadVersion; var key = loadedKey; var workspace = WorkspaceId; busy = true; ++readVersion; error = null;
        try { var cancelled = await Interactions.CancelTaskAsync(workspace, current.Id, lifetimeToken); if (!Current(revision, key)) return; ValidateTask(cancelled, workspace, current.Id, currentInteraction.Id); activeTask = cancelled; await RefreshInteractionAsync(); }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(revision, key)) error = T("CancelFailed"); }
        finally { if (Current(revision, key)) busy = false; }
    }
    private Task NewRequestAsync() { if (!disposed && !busy && !loading) Navigation.NavigateTo(ConsoleEntryInteractionNavigation.Build(WorkspaceId, ResourceNamespace.Parse(EntryNamespace), EntryName)); return Task.CompletedTask; }
    private async Task RefreshInteractionAsync()
    {
        if (disposed || interaction is not { } current || entry is not { } definition) return;
        var revision = loadVersion; var key = loadedKey; var read = ++readVersion;
        try { var bundle = await ReadInteractionAsync(WorkspaceId, current.Id, definition, revision, key, read); if (bundle is not null) { AdoptOrBuffer(bundle); loadState = new(ResourceLoadStatus.Ready) { HasContent = true }; } }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (CurrentRead(revision, key, read)) ReadFailed(exception); }
    }
    private Task OnRealtimeAsync(WorkOperationsRealtimeUpdate update) => !disposed && !busy && !loading && (update.RequiresResynchronization || update.InteractionId is { } interactionId && interactionId == interaction?.Id || update.TaskId is { } taskId && taskId == activeTask?.Id)
        ? InvokeAsync(async () => { await RefreshInteractionAsync(); if (!disposed) StateHasChanged(); }) : Task.CompletedTask;
    private void ApplyContinuation(InteractionResponse updated, WorkplaceAction action, WorkTaskResponse? task) { interaction = updated; messages = updated.Messages; currentAction = action; activeTask = task; messagesKnown = true; }
    private void DiscardDrafts()
    {
        if (disposed || busy || loading) return; if (newerEntry is not null) entry = newerEntry; newerEntry = null;
        if (newerInteraction is not null) Adopt(newerInteraction);
        initialValues = new Dictionary<string, JsonElement>(); requestDraft = conversationDraft = false; ++initialEditorVersion; ++conversationEditorVersion; ++responseAcknowledgement; error = null;
    }
    private string ArtifactUrl(WorkTaskArtifact artifact) => Interactions.GetArtifactContentUri(WorkspaceId, artifact.WorkTaskId.Value, artifact.Id.Value).ToString();
    private static string TaskUrl(WorkTaskResponse task) => $"/tasks/{task.Id:D}";
    private static IReadOnlyDictionary<string, JsonElement> InitialValues(EntryResponse value, string? query)
    {
        if (query is null) return new Dictionary<string, JsonElement>(); var primary = value.Presentation.Fields.SingleOrDefault(field => field.Role == EntryFieldRole.PrimaryInput);
        return primary is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [primary.Name] = JsonSerializer.SerializeToElement(query) };
    }
    private static bool CanSubmitInitialQuery(EntryResponse value)
    {
        var primary = value.Presentation.Fields.SingleOrDefault(field => field.Role == EntryFieldRole.PrimaryInput);
        return primary is not null && primary.Type is EntryFieldType.Prompt or EntryFieldType.Text or EntryFieldType.Textarea or EntryFieldType.Conversation && value.Presentation.Fields.All(field => !field.Required || field.Name == primary.Name);
    }
    private static EntryResource ToDefinition(EntryResponse value) => new() { WorkspaceId = new(value.WorkspaceId), Id = new(value.Id, value.Namespace), Name = value.Name, DisplayName = value.DisplayName, Description = value.Description, Presentation = value.Presentation, Exposure = value.Exposure, ResolvedTarget = value.ResolvedTarget, Behavior = value.Behavior, ApiVersion = value.ApiVersion, Type = value.Type, Version = value.Version, PublishedAt = value.PublishedAt };
    public void Dispose() { if (disposed) return; disposed = true; ++loadVersion; ++readVersion; if (subscribed) { ContextState.Changed -= ContextChanged; Realtime.Updated -= OnRealtimeAsync; } lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this); }
    private sealed record InteractionBundle(InteractionResponse Interaction, IReadOnlyList<ConversationMessage> Messages, PendingActionContract? Pending, WorkplaceAction? Action, WorkTaskResponse? Task, IReadOnlyList<WorkTaskActivity> Activities, IReadOnlyList<WorkTaskResult> Results, IReadOnlyList<WorkTaskArtifact> Artifacts);
}
