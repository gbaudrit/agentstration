using System.Net;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class FlowDetails
{
    [Parameter] public string FlowId { get; set; } = string.Empty;
    [Parameter] public string? FlowNamespace { get; set; }
    [SupplyParameterFromQuery(Name = "scopeRef")] public string? ScopeRefValue { get; set; }
    [SupplyParameterFromQuery(Name = "namespace")] public string? NamespaceValue { get; set; }
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken lifetimeToken;
    private readonly ResourceSectionState sectionState = new();
    private FlowResponse? flow;
    private FlowResourceSnapshot? snapshot, newerSnapshot;
    private FlowOrchestrationEditor? orchestrationEditor;
    private IReadOnlyList<FlowVersionResponse> versions = [];
    private IReadOnlyList<FlowRun> runs = [];
    private FlowDraftResponse? definitionDraft, newerDefinitionDraft;
    private FlowDefinitionEditorModel definitionModel = new();
    private EditContext? definitionContext;
    private ValidationMessageStore? definitionMessages;
    private ResourceDefinitionForm? definitionForm;
    private string runVersion = string.Empty;
    private string? loadedKey, error, definitionError, definitionBaseline;
    private bool loading, busy, showRun, submitting, runInputValid = true, orchestrationDirty, orchestrationSaving;
    private bool definitionLoaded, definitionLoading, definitionSaving, definitionSaved, definitionInvalid;
    private bool versionsKnown, runsKnown, disposed, subscribed;
    private long loadVersion;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private JsonElement runInput = JsonSerializer.SerializeToElement(new { });
    private string runInputBaseline = "{}";

    public FlowDetails() => lifetimeToken = cancellation.Token;
    private string ContextKey => $"{FlowId}|{RequestedNamespace}|{ScopeRefValue}|{ResourceOwnershipContext.TryGetCurrent(Services)}|{ContextState.Current?.WorkspaceId}|{ContextState.Current?.UserId}";
    private ResourceNamespace RequestedNamespace => ResourceNamespace.Parse(FlowNamespace ?? NamespaceValue);
    private ResourceNamespace FlowResourceNamespace => flow?.Namespace ?? RequestedNamespace;
    private ResourceScopeRef FlowScopeRef => flow!.ScopeRef!.Value;
    private bool IsOwnerContext => ResourceOwnershipContext.CanManage(flow?.ScopeRef, Services);
    private bool IsEditable => !disposed && flow is { Governance.IsManaged: false } && IsOwnerContext && ContextState.HasPermission("resources/write");
    private bool CanExecute => !disposed && ContextState.HasPermission("runs/execute");
    private bool Busy => loading || busy || definitionSaving || orchestrationSaving;
    private bool IsOrchestration => flow?.Definition is OrchestrationFlowDefinition;
    private OrchestrationFlowDefinition? Orchestration => flow?.Definition as OrchestrationFlowDefinition;
    private string? PublishedVersion => flow?.ActiveVersion ?? versions.OrderByDescending(value => value.PublishedAt).FirstOrDefault()?.Version;
    private bool HasPublishedVersion => PublishedVersion is not null;
    private string StatusLabel => T(HasPublishedVersion ? "Status.Published" : "Status.Draft");
    private string FlowYaml => flow is null ? string.Empty : FlowYamlPresentation.ToYaml(flow);
    private string EditorUrl => ResourceOwnershipContext.PreserveNavigation(
        $"{(FlowResourceNamespace.IsDefault ? "/flows" : $"/namespaces/{Uri.EscapeDataString(FlowResourceNamespace.Value)}/flows")}/{Uri.EscapeDataString(FlowId)}/designer?scopeRef={Uri.EscapeDataString(FlowScopeRef.Value)}", Services);
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections => [new("runs", T("Tab.Runs"), "activity"), new("deployments", T("Tab.Deployments"), "history")];
    private bool DefinitionDirty => definitionDraft is not null && DefinitionFingerprint != definitionBaseline;
    private string DefinitionFingerprint => JsonSerializer.Serialize(new { DisplayName = definitionModel.DisplayName.Trim(), Description = NormalizeDescription(definitionModel.Description), definitionModel.Tags });
    private bool HasDefinitionDraft => DefinitionDirty || definitionInvalid || definitionContext?.GetValidationMessages().Any() == true || orchestrationDirty || orchestrationEditor?.HasUnsavedChanges == true;
    private bool HasNewerSnapshot => newerSnapshot is not null || newerDefinitionDraft is not null;
    private bool HasRunDraft => showRun && (runInput.GetRawText() != runInputBaseline || !runInputValid);
    private bool CanEditDefinition => IsEditable && definitionDraft is not null;
    private ResourceReadOnlyReason? ReadOnlyReason => flow is null ? null : !IsOwnerContext ? ResourceReadOnlyReason.Inherited
        : flow.Governance.IsManaged ? ResourceReadOnlyReason.Managed : !ContextState.HasPermission("resources/write") ? ResourceReadOnlyReason.InsufficientPermissions
        : !IsOrchestration && definitionLoaded && definitionDraft is null ? ResourceReadOnlyReason.Unspecified : null;
    private static string? NormalizeDescription(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private FlowVersionResponse? SelectedRunVersion => versions.SingleOrDefault(version => version.Version == runVersion);
    private FlowTopologyGraph Topology => flow is null ? new([], [], "horizontal", T("FlowTopology")) : FlowTopologyProjector.Project(flow.Definition, flow.Graph);
    private FlowTopologyGraph PreviewTopology => flow is null ? new([], [], "horizontal", T("FlowTopology")) : FlowTopologyProjector.ProjectPreview(flow.Definition, flow.Graph);
    private int TopologyNodeCount => Topology.Nodes.Count(node => node.Type is not "input" and not "output" and not "conversation" and not "manager");
    private string T(string key, params object[] arguments) => Localizer[key, arguments];
    private string StrategyLabel(FlowOrchestrationStrategy strategy) => Localizer[$"Strategy.{strategy}"];
    private string SucceededRunCount(int count) => T(count == 1 ? "SucceededRunCount.One" : "SucceededRunCount.Many", count);
    private sealed record SavedSetting(string Label, string Value);
    private IReadOnlyList<SavedSetting> SavedConfiguration => flow is null ? [] : [new(T("Kind"), FlowLabels[$"Kind.{flow.Definition.Kind}"]), new(Shared["Enabled"], Shared[flow.Enabled ? "Enabled" : "Disabled"]), new(T("PublishedVersion"), PublishedVersion ?? "—"), new(T("LastPublication"), !versionsKnown || versions.Count == 0 ? "—" : versions.Max(value => value.PublishedAt).LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture))];

    // Flow owns its repository and does not expose a Core Resource document. This presentation-only
    // envelope carries its existing identity/policy coordinates and published metadata verbatim.
    // Generation remains zero, matching FlowResourceIdentityContributor; it is never a UI counter.
    private sealed record FlowIdentityPresentation : Resource;
    private Resource? SavedIdentity => flow is null ? null : new FlowIdentityPresentation
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = FlowResourceKinds.Flow, Uid = flow.Uid ?? Guid.Empty,
        Metadata = new() { Name = flow.Id, Namespace = flow.Namespace, Tags = flow.Metadata },
        ScopeRef = flow.ScopeRef, Governance = flow.Governance, ETag = snapshot?.ETag
    };

    protected override async Task OnParametersSetAsync()
    {
        await ContextState.EnsureLoadedAsync(lifetimeToken);
        if (disposed) return;
        if (!subscribed) { ContextState.Changed += ContextChanged; subscribed = true; }
        if (loadedKey == ContextKey) return;
        ++loadVersion; ClearPrivate(); loadedKey = ContextKey; sectionState.Reset(loadedKey);
        await LoadAsync();
    }
    private void ContextChanged() => _ = InvokeAsync(async () =>
    {
        if (disposed) return;
        if (loadedKey != ContextKey)
        {
            ++loadVersion; ClearPrivate(); loadedKey = ContextKey; sectionState.Reset(loadedKey); await LoadAsync();
        }
        else if (!ContextState.HasPermission("resources/read")) { ++loadVersion; ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); }
        StateHasChanged();
    });
    private bool Current(long revision, string? key) => !disposed && revision == loadVersion && key == loadedKey && key == ContextKey;
    private static ResourceLoadStatus FailureStatus(Exception exception) => exception is AgentstrationApiException api ? api.StatusCode switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden,
        HttpStatusCode.NotFound => ResourceLoadStatus.NotFound, _ => ResourceLoadStatus.Failed
    } : ResourceLoadStatus.Failed;
    private void ClearPrivate()
    {
        flow = null; snapshot = newerSnapshot = null; versions = []; runs = []; versionsKnown = runsKnown = false;
        definitionDraft = newerDefinitionDraft = null; definitionForm = null; orchestrationEditor = null;
        DetachDefinitionContext(); definitionModel = new(); definitionBaseline = null;
        error = definitionError = null; definitionLoaded = definitionLoading = definitionSaving = definitionSaved = definitionInvalid = false;
        busy = showRun = submitting = orchestrationDirty = orchestrationSaving = false;
    }
    private Task RefreshAsync() => Busy ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync()
    {
        var revision = ++loadVersion; var key = loadedKey; var id = FlowId; var resourceNamespace = RequestedNamespace;
        var owner = flow?.ScopeRef ?? (string.IsNullOrWhiteSpace(ScopeRefValue) ? null : ResourceScopeRef.Parse(ScopeRefValue));
        loading = true; error = null;
        loadState = new(flow is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = flow is not null };
        try
        {
            if (!ContextState.HasPermission("resources/read")) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            FlowResourceSnapshot saved;
            if (owner is { } exact) saved = await Client.GetFlowSnapshotAsync(resourceNamespace, id, exact, lifetimeToken);
            else
            {
                var visible = await Client.GetFlowAsync(resourceNamespace, id, null, lifetimeToken);
                if (!Current(revision, key)) return;
                ValidateIdentity(visible, id, resourceNamespace, null);
                saved = await Client.GetFlowSnapshotAsync(resourceNamespace, id, visible.ScopeRef!.Value, lifetimeToken);
                if (!SameIdentity(saved.Value, visible)) throw new InvalidOperationException(T("IdentityMismatch"));
            }
            if (!Current(revision, key)) return;
            ValidateIdentity(saved.Value, id, resourceNamespace, owner);
            if (flow is not null && !SameIdentity(saved.Value, flow)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            var changed = snapshot is null || snapshot.ETag != saved.ETag || !Equivalent(saved.Value, snapshot.Value);
            if (HasDefinitionDraft) newerSnapshot = changed ? saved : null;
            else if (changed)
            {
                snapshot = saved; flow = saved.Value; newerSnapshot = null;
                if (IsOrchestration && orchestrationEditor is not null) await orchestrationEditor.ApplySavedSnapshotAsync(saved);
            }
            if (!Current(revision, key)) return;
            if (!IsOrchestration && definitionLoaded) await RefreshDefinitionAsync(revision, key);
            if (!Current(revision, key)) return;
            await LoadHistoryAsync(revision, key);
            if (!Current(revision, key)) return;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = flow is not null };
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            if (!Current(revision, key)) return;
            var status = exception is InvalidOperationException or ArgumentException ? ResourceLoadStatus.NotFound : FailureStatus(exception);
            if (status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearPrivate();
            loadState = new(status) { HasContent = flow is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
        finally { if (Current(revision, key)) loading = false; }
    }
    private async Task LoadHistoryAsync(long revision, string? key)
    {
        if (flow is null) return;
        var current = flow; var physical = ResourceScopeRef.Workspace(ContextState.Current!.WorkspaceId);
        var historyTask = Client.GetFlowVersionsAsync(current.Namespace, current.Id, current.ScopeRef, lifetimeToken);
        var recentTask = Client.GetFlowRunsAsync(current.Namespace, current.Id, current.ScopeRef, physical, lifetimeToken);
        try { await Task.WhenAll(historyTask, recentTask); }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { /* Each family result is classified below. */ }
        try
        {
            var incoming = await historyTask; if (!Current(revision, key)) return;
            if (incoming.Any(value => value.FlowId != current.Id || value.Namespace != current.Namespace || value.ScopeRef != current.ScopeRef)) throw new InvalidOperationException(T("IdentityMismatch"));
            versions = incoming; versionsKnown = true;
            if (SelectedRunVersion is null) runVersion = current.ActiveVersion ?? versions.FirstOrDefault()?.Version ?? string.Empty;
        }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException)
        {
            if (!Current(revision, key)) return;
            if (FailureStatus(exception) is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) throw;
            versionsKnown = false; error = T("HistoryUnavailable");
        }
        try
        {
            var incoming = await recentTask; if (!Current(revision, key)) return;
            if (incoming.Any(value => value.FlowId.Value != current.Id || value.FlowId.Namespace != current.Namespace || (value.DefinitionScopeRef ?? value.DefinitionSnapshot.ScopeRef) != current.ScopeRef || value.ExecutionScopeRef != physical)) throw new InvalidOperationException(T("IdentityMismatch"));
            runs = incoming; runsKnown = true;
        }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException)
        {
            if (!Current(revision, key)) return;
            runsKnown = false; error = T("HistoryUnavailable");
        }
    }
    private void ValidateIdentity(FlowResponse value, string id, ResourceNamespace resourceNamespace, ResourceScopeRef? owner)
    {
        if (value.Id != id || value.Namespace != resourceNamespace || value.ScopeRef is null || owner is not null && value.ScopeRef != owner)
            throw new InvalidOperationException(T("IdentityMismatch"));
    }
    private static bool SameIdentity(FlowResponse value, FlowResponse expected) => value.Id == expected.Id && value.Namespace == expected.Namespace && value.ScopeRef == expected.ScopeRef
        && (expected.Uid is not { } uid || uid == Guid.Empty || value.Uid == uid);
    private static bool Equivalent(FlowResponse actual, FlowResponse expected) => JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expected);
    private async Task SelectSectionAsync(string? path)
    {
        if (path?.StartsWith("definition/", StringComparison.Ordinal) == true && !IsOrchestration) await LoadDefinitionAsync();
    }
    private Task ShowSectionAsync(string path) { Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path); return Task.CompletedTask; }
    private async Task LoadDefinitionAsync()
    {
        if (definitionLoaded || definitionLoading || flow is null) return;
        var revision = loadVersion; var key = loadedKey; definitionLoading = true; definitionError = null;
        try { await RefreshDefinitionAsync(revision, key); }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException)
        {
            if (!Current(revision, key)) return;
            if (FailureStatus(exception) is ResourceLoadStatus.Forbidden) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            definitionError = exception.Message;
        }
        catch (InvalidOperationException) { if (Current(revision, key)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); } }
        finally { if (Current(revision, key)) { definitionLoading = false; definitionLoaded = true; } }
    }
    private async Task RefreshDefinitionAsync(long revision, string? key)
    {
        var current = flow; if (current is null) return;
        if (!IsEditable) { if (definitionContext is null) ResetDefinition(null); return; }
        FlowDraftResponse? incoming;
        try { incoming = await Client.GetDraftAsync(current.Namespace, current.Id, current.ScopeRef!.Value, lifetimeToken); }
        catch (AgentstrationApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            if (!Current(revision, key)) return;
            definitionError = T("DefinitionDraftUnavailable"); if (definitionContext is null) ResetDefinition(null); return;
        }
        if (!Current(revision, key)) return;
        ValidateDraft(incoming, current);
        if (HasDefinitionDraft) newerDefinitionDraft = incoming.ETag != definitionDraft?.ETag ? incoming : null;
        else if (definitionContext is null || incoming.ETag != definitionDraft?.ETag) ResetDefinition(incoming);
    }
    private void ValidateDraft(FlowDraftResponse value, FlowResponse expected)
    {
        if (value.Value.FlowId.Value != expected.Id || value.Value.FlowId.Namespace != expected.Namespace || value.Value.ScopeRef != expected.ScopeRef
            || value.Value.WorkspaceId.Value != ContextState.Current!.WorkspaceId) throw new InvalidOperationException(T("IdentityMismatch"));
    }
    private void DetachDefinitionContext() { if (definitionContext is not null) definitionContext.OnFieldChanged -= DefinitionFieldChanged; definitionContext = null; definitionMessages = null; }
    private void ResetDefinition(FlowDraftResponse? draft)
    {
        if (flow is null) return;
        DetachDefinitionContext(); definitionDraft = draft; newerDefinitionDraft = null; definitionModel = FlowDefinitionEditorModel.From(flow, draft);
        definitionContext = new(definitionModel); definitionMessages = new(definitionContext); definitionContext.OnFieldChanged += DefinitionFieldChanged;
        definitionBaseline = DefinitionFingerprint; definitionInvalid = definitionSaved = false;
    }
    private void DefinitionFieldChanged(object? sender, FieldChangedEventArgs args) { definitionMessages?.Clear(args.FieldIdentifier); definitionContext?.NotifyValidationStateChanged(); _ = InvokeAsync(StateHasChanged); }
    private async Task SaveDefinitionAsync(EditContext context)
    {
        if (!CanEditDefinition || Busy || !HasDefinitionDraft || definitionInvalid || !ReferenceEquals(context, definitionContext)) return;
        definitionMessages!.Clear();
        if (!definitionModel.HasValidDisplayName)
        {
            definitionMessages.Add(context.Field(nameof(definitionModel.DisplayName)), Shared["Validation.StringLength", T("DisplayName"), 200]);
            context.NotifyValidationStateChanged();
            if (definitionForm is not null) await definitionForm.RevealValidationAsync();
            return;
        }
        IReadOnlyDictionary<string, string> tags;
        try { tags = definitionModel.Tags == FlowDefinitionEditorModel.Format(definitionDraft!.Value.Tags) ? definitionDraft.Value.Tags : definitionModel.TagValues(); }
        catch (ArgumentException) { definitionMessages.Add(context.Field(nameof(definitionModel.Tags)), Shared["LabelInvalid"]); context.NotifyValidationStateChanged(); if (definitionForm is not null) await definitionForm.RevealValidationAsync(); return; }
        var revision = loadVersion; var key = loadedKey; var original = definitionDraft!; var current = flow!;
        definitionSaving = true; definitionError = null; definitionSaved = false;
        try
        {
            var saved = await Client.SaveDraftAsync(current.Namespace, current.Id, current.ScopeRef!.Value,
                new(definitionModel.DisplayName.Trim(), NormalizeDescription(definitionModel.Description), tags, original.Value.Definition), original.ETag, lifetimeToken);
            if (!Current(revision, key)) return;
            ValidateDraft(saved, current); ResetDefinition(saved); definitionSaved = true;
            // A metadata-draft save does not publish or overwrite the saved definition envelope.
        }
        catch (AgentstrationApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        { if (Current(revision, key)) definitionError = T("DefinitionSaveConflict"); }
        catch (Exception exception) when (exception is not OperationCanceledException) { if (Current(revision, key)) definitionError = exception.Message; }
        finally { if (Current(revision, key)) definitionSaving = false; }
    }
    private async Task DiscardDefinitionAsync()
    {
        if (Busy) return;
        var revision = loadVersion; var key = loadedKey; var replacement = newerSnapshot;
        if (replacement is not null) { snapshot = replacement; flow = replacement.Value; newerSnapshot = null; }
        if (IsOrchestration)
        {
            if (replacement is not null && orchestrationEditor is not null) await orchestrationEditor.ApplySavedSnapshotAsync(replacement);
            else orchestrationEditor?.Discard();
            orchestrationDirty = false;
        }
        else
        {
            ResetDefinition(newerDefinitionDraft ?? definitionDraft);
            if (replacement is not null && definitionLoaded) await RefreshDefinitionAsync(revision, key);
        }
        definitionError = null;
    }
    private async Task ToggleEnabledAsync(bool value)
    {
        if (Busy || !IsEditable || snapshot is null) return;
        var current = snapshot; var revision = loadVersion; var key = loadedKey; busy = true; error = null;
        try
        {
            var saved = await Client.UpdateFlowAsync(current.Value.Namespace, current.Value.Id, current.Value.ScopeRef!.Value,
                new(current.Value.Description, current.Value.Version, value, current.Value.Definition, current.Value.Metadata) { DisplayName = current.Value.DisplayName }, current.ETag, lifetimeToken);
            if (!Current(revision, key)) return;
            if (!SameIdentity(saved.Value, current.Value)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            snapshot = saved; flow = saved.Value; newerSnapshot = null; orchestrationEditor?.ApplyActivationSnapshot(saved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { if (Current(revision, key)) error = exception.Message; }
        finally { if (Current(revision, key)) busy = false; }
    }
    private string? CurrentRenderIdentity => flow is null ? null : $"{loadedKey}|{flow.Uid}|{loadVersion}";
    private void OrchestrationDraftChanged(string? identity, bool value) { if (!disposed && flow is not null && identity == CurrentRenderIdentity && loadedKey == ContextKey) orchestrationDirty = value; }
    private void OrchestrationSavingChanged(string? identity, bool value) { if (!disposed && flow is not null && identity == CurrentRenderIdentity && loadedKey == ContextKey) orchestrationSaving = value; }
    private async Task OrchestrationSavedAsync(string? identity, FlowResourceSnapshot value)
    {
        if (disposed || identity != CurrentRenderIdentity || loadedKey != ContextKey || snapshot is null || !SameIdentity(value.Value, snapshot.Value)) return;
        var changedPublication = value.Value.ActiveVersion != snapshot.Value.ActiveVersion; var revision = loadVersion; var key = loadedKey;
        snapshot = value; flow = value.Value; newerSnapshot = null;
        if (changedPublication)
        {
            try { await LoadHistoryAsync(revision, key); }
            catch (Exception exception) when (exception is not OperationCanceledException) { if (Current(revision, key)) error = exception.Message; }
        }
    }
    private void OpenRun() { if (!CanExecute || versions.Count == 0 || flow is null) return; showRun = true; error = null; runInputValid = true; runInput = ParseInput(flow.Metadata.GetValueOrDefault("agentstration.io/sample-input") ?? "{}"); runInputBaseline = runInput.GetRawText(); }
    private void ChangeRunVersion(string? value) { runVersion = value ?? string.Empty; error = null; }
    private Task UpdateRunInputValidation(JsonSchema.JsonSchemaInputValidationState value) { runInputValid = value.IsValid; return Task.CompletedTask; }
    private async Task SubmitRunAsync()
    {
        if (!runInputValid || !CanExecute || submitting || SelectedRunVersion is null || flow is null) return;
        var revision = loadVersion; var key = loadedKey; var current = flow; submitting = true; error = null;
        try
        {
            var run = await Client.CreateFlowRunAsync(current.Namespace, current.Id, new(runInput.Clone(), runVersion, "local", FlowRunTrigger.Manual) { DefinitionScopeRef = current.ScopeRef }, lifetimeToken);
            if (Current(revision, key)) { showRun = false; Navigation.NavigateTo(ResourceOwnershipContext.PreserveNavigation($"/flow-runs/{Uri.EscapeDataString(run.Id)}?scopeRef={Uri.EscapeDataString(run.ExecutionScopeRef.Value)}", Services)); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { if (Current(revision, key)) error = exception.Message; }
        finally { if (Current(revision, key)) submitting = false; }
    }
    private static JsonElement ParseInput(string value)
    {
        try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : JsonSerializer.SerializeToElement(new { }); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { }); }
    }
    public void Dispose()
    {
        disposed = true; ++loadVersion;
        if (subscribed) ContextState.Changed -= ContextChanged;
        DetachDefinitionContext(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this);
    }
}
