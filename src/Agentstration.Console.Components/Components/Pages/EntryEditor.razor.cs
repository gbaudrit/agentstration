using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Pages;

public partial class EntryEditor
{
    [Parameter] public string Name { get; set; } = "new";
    [Parameter] public string? EntryNamespace { get; set; }
    [SupplyParameterFromQuery(Name = "scopeRef")] public string? RequestedOwner { get; set; }
    private readonly CancellationTokenSource cancellation = new();
    private readonly ResourceSectionState sectionState = new();
    private EntryEditorModel model = new();
    private EditContext formContext;
    private ValidationMessageStore messages;
    private ResourceDefinitionForm? definitionForm;
    private CreationWizard? wizard;
    private ConsoleContextState? contextState;
    private EntryDraftResponse? saved, newerEntry;
    private EntryResource? published => saved?.Published;
    private EntryBinding? publishedBinding => saved?.Value.PublishedBinding;
    private long draftRevision => saved?.Value.Revision ?? 1;
    private EntryBinding declaredBinding = new(EntryBindingKind.Agent, "");
    private bool revealValidation, bindingEdited, loading, busy, hasError, interactive, invalidInput, disposed, usageLoaded, subscribed;
    private string message = "", rawYaml = "", rawBaseline = "", baseline = "";
    private string? rawError, loadedKey, pendingValidationStep;
    private long loadVersion, resourceVersion;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private EntryValidationResponse? validation;
    private IReadOnlyList<ResourcePickerItem> resources = [];
    private IReadOnlyList<EntryDependencyResponse> dependencies = [];
    private IReadOnlyList<WorkplaceWorkspaceResponse> workspaces = [];
    private IReadOnlyList<WorkplaceDashboardDraftResponse> dashboards = [];
    private bool IsNew => string.Equals(Name, "new", StringComparison.OrdinalIgnoreCase);
    private ResourceNamespace EntryResourceNamespace => ResourceNamespace.Parse(EntryNamespace);
    private bool IsPackOwned => saved?.ManagedByPack == true;
    private bool CanWrite => !disposed && !IsPackOwned && (contextState is null || contextState.Current is not null && contextState.HasPermission("resources/write"));
    private ResourceReadOnlyReason? ReadOnlyReason => IsPackOwned ? ResourceReadOnlyReason.Managed : !CanWrite ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private string AddressKey => $"{Name}|{RequestedOwner}|{EntryResourceNamespace}|{ResourceOwnershipContext.TryGetCurrent(Services)}|{contextState?.Current?.UserId}|{contextState?.Current?.WorkspaceId}";
    private string Fingerprint => JsonSerializer.Serialize(new { model, bindingEdited, declaredBinding });
    private bool HasModelDraft => baseline.Length > 0 && (baseline != Fingerprint || invalidInput);
    private bool HasRawDraft => rawBaseline != rawYaml;
    private bool HasDraft => HasModelDraft || HasRawDraft;
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private string EnumLabel<TValue>(string group, TValue value) where TValue : struct, Enum => T($"{group}.{value}");
    private string BackUrl => ResourceOwnershipContext.PreserveNavigation("/entries", Services);
    private string WorkspaceDisplayName => workspaces.FirstOrDefault(value => value.Id == (saved?.Value.WorkspaceId.Value ?? model.WorkspaceId.Value))?.DisplayName ?? contextState?.Current?.WorkspaceDisplayName ?? (saved?.Value.WorkspaceId.Value ?? model.WorkspaceId.Value).ToString("D");
    private string WorkspaceConfigurationUrl => ResourceOwnershipContext.PreserveNavigation($"/workspaces?entry={Uri.EscapeDataString(saved?.Value.Name ?? model.Name)}&entryNamespace={Uri.EscapeDataString(EntryResourceNamespace.Value)}", Services);
    private IReadOnlyList<WorkplaceDashboardDraftResponse> PublishedUsages => dashboards.Where(value => UsageReference(value) is not null).ToArray();
    private IReadOnlyList<WorkplaceDashboardDraftResponse> PrimaryUsages => PublishedUsages.Where(value => UsageReference(value)?.Role == DashboardItemRole.Primary).ToArray();
    private DashboardEntryReference? UsageReference(WorkplaceDashboardDraftResponse dashboard) => dashboard.Published?.Entries.FirstOrDefault(value => value.EntryResourceId == new EntryId(saved?.Value.Name ?? model.Name, EntryResourceNamespace));
    private ResourcePickerItem? SelectedResource
    {
        get
        {
            var exact = resources.Where(value => ResourceKey(value) == model.ResourceId).ToArray();
            if (exact.Length == 1) return exact[0];
            if (bindingEdited) return null;
            var candidates = resources.Where(value => value.ResourceId == declaredBinding.ResourceId && value.Namespace == (declaredBinding.Namespace ?? EntryResourceNamespace) && (declaredBinding.ScopeRef is null || value.ScopeRef == declaredBinding.ScopeRef)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
    private string? SelectedModelProfile => SelectedResource?.Details?.GetValueOrDefault("modelProfile");
    private string BindingResourceName => SelectedResource?.Name ?? (string.IsNullOrWhiteSpace(model.ResourceId) ? T("NotSelected") : FriendlyResourceName(model.ResourceId));
    private string ResolvedFlowName => published is null ? T("ResolvedOnPublication") : FriendlyResourceName(published.ResolvedTarget.FlowResourceId);
    private string PublishedBindingSummary => publishedBinding is null ? T("NotRetained") : BindingText(publishedBinding);
    private EntryResource PreviewDefinition => new() { WorkspaceId = model.WorkspaceId, Id = new(string.IsNullOrWhiteSpace(model.Name) ? "preview" : model.Name), Name = string.IsNullOrWhiteSpace(model.Name) ? "preview" : model.Name, DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? T("EntryPreview") : model.DisplayName, Description = model.Description, Presentation = model.Presentation(), Exposure = model.Exposure(), ResolvedTarget = published?.ResolvedTarget ?? new("preview", "1.0.0"), Behavior = model.Behavior() };
    public EntryEditor() { formContext = new(model); messages = new(formContext); }
    private IReadOnlyDictionary<string, ResourceSectionPresentation> Presentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { ReadOnly = !CanWrite, HasRequiredFields = true, Fields = [nameof(model.Name), nameof(model.DisplayName), nameof(model.Description), nameof(model.Namespace), nameof(model.WorkspaceId)] },
        ["definition/configuration"] = new() { ReadOnly = !CanWrite, HasRequiredFields = true, Actions = CanWrite ? ValidateActions : null, Fields = [nameof(model.BindingKind), nameof(model.ResourceId), nameof(model.ExposeWorkplace), nameof(model.ExposeConsole), nameof(model.ExposeOwningSpace), nameof(model.ExposeTenantHome), nameof(model.ConsoleFallback), nameof(model.TaskMode), nameof(model.AllowConversation), nameof(model.StreamResponse)] },
        ["definition/presentation"] = new() { ReadOnly = !CanWrite, Fields = [nameof(model.PresentationKind), nameof(model.Placeholder), nameof(model.Icon), nameof(model.AllowAttachments), nameof(model.AllowVoiceInput), nameof(model.ParticipantsVisibility), nameof(model.ProgressVisibility), nameof(model.TaskDisplay), nameof(model.ResultDisplay)] },
        ["definition/fields"] = new() { ReadOnly = !CanWrite, HasRequiredFields = true, Fields = [nameof(model.Fields)] },
        ["definition/suggestions"] = new() { ReadOnly = !CanWrite, Fields = [nameof(model.Suggestions)] },
        ["yaml"] = new() { ReadOnly = !CanWrite, Actions = CanWrite ? YamlActions : null }
    };
    private IReadOnlyDictionary<string, string> FieldLabels => new Dictionary<string, string>(StringComparer.Ordinal) { [nameof(model.Name)] = T("Name"), [nameof(model.DisplayName)] = T("DisplayName") };
    private IReadOnlyList<ResourceNavigationItem> DefinitionSections => [new("definition/presentation", T("WorkplaceAppearance"), "layout-grid"), new("definition/fields", T("InputSchema"), "forms"), new("definition/suggestions", T("PromptStarters"), "message")];
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections => [new("preview", T("Preview"), "eye"), new("usage", T("Usage"), "link"), new("versions", T("Versions"), "history")];
    private RenderFragment<string> DefinitionContent => path => path switch { "definition/identity" => IdentityFields, "definition/presentation" => PresentationFields, "definition/fields" => InputSchemaFields, "definition/suggestions" => SuggestionsFields, _ => ConfigurationFields };
    private IReadOnlyList<CreationWizardStep> Steps => [new("identity", T("Identity"), IdentityFields) { IsComplete = IdentityComplete }, new("configuration", Shared["Configuration"], ConfigurationFields) { IsComplete = ConfigurationComplete }, new("presentation", T("WorkplaceAppearance"), PresentationFields) { IsComplete = true }, new("fields", T("InputSchema"), InputSchemaFields) { IsComplete = FieldsComplete }, new("suggestions", T("PromptStarters"), SuggestionsFields) { IsComplete = SuggestionsComplete }, new("review", Shared["Verification"], Review) { IsComplete = IdentityComplete && ConfigurationComplete && FieldsComplete && SuggestionsComplete }];
    private bool IdentityComplete => !string.IsNullOrWhiteSpace(model.Name) && !string.IsNullOrWhiteSpace(model.DisplayName) && model.Name.Length <= 128;
    private bool ConfigurationComplete => model.ResourceId.Length > 0 && (model.ExposeWorkplace || model.ExposeConsole) && (!model.ExposeWorkplace || model.ExposeOwningSpace || model.ExposeTenantHome);
    private bool FieldsComplete => model.Fields.Count > 0 && model.Fields.All(value => !string.IsNullOrWhiteSpace(value.Name)) && model.Fields.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() == model.Fields.Count && model.Fields.Count(value => value.Primary) == 1;
    private bool SuggestionsComplete => model.Suggestions.All(value => !string.IsNullOrWhiteSpace(value.Label) && !string.IsNullOrWhiteSpace(value.Value)) && model.Suggestions.Select(value => value.Label).Distinct(StringComparer.Ordinal).Count() == model.Suggestions.Count;
    private Task StepReadyAsync(string id) { if (pendingValidationStep != id || definitionForm is null) return Task.CompletedTask; pendingValidationStep = null; definitionForm.RequestValidationFocus(); return InvokeAsync(StateHasChanged); }
    private async Task FinishAsync(CreationWizardFinish finish) { if (!await ValidateFormAsync()) { finish.ReturnToStepId = InvalidStep(); pendingValidationStep = finish.ReturnToStepId; return; } await ExecuteCommandAsync(EntryCommand.Publish); }
    private string InvalidStep() => ResourceDefinitionForm.FirstInvalidSection(formContext, Presentations)?.Split('/').Last() ?? "identity";
    private void ShowSection(string path) => Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path);
    private Task SelectSectionAsync(string? path) { if (path == "yaml" && !HasRawDraft) PrepareRawYaml(); return Task.CompletedTask; }
    protected override void OnInitialized() => contextState = Services.GetService<ConsoleContextState>();
    protected override Task OnParametersSetAsync()
    {
        if (loadedKey == AddressKey) return Task.CompletedTask;
        loadedKey = AddressKey; ClearPrivate(); sectionState.Reset(loadedKey); return LoadAsync();
    }
    private void ContextChanged() => _ = InvokeAsync(async () =>
    {
        if (disposed) return;
        if (loadedKey != AddressKey) { ++loadVersion; loadedKey = AddressKey; ClearPrivate(); sectionState.Reset(loadedKey); await LoadAsync(); }
        else if (contextState?.Current is not null && !contextState.HasPermission("resources/read")) { ++loadVersion; ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); }
        // Permission-only changes retain the draft and immediately update the common read-only presentation.
        StateHasChanged();
    });
    private bool Current(long version, string? key) => !disposed && version == loadVersion && key == loadedKey && key == AddressKey && (contextState?.Current is null || contextState.HasPermission("resources/read"));
    private Task RefreshAsync() => disposed || busy || loading ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync()
    {
        var version = ++loadVersion; var key = loadedKey; loading = true; message = ""; loadState = new(saved is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = saved is not null };
        try
        {
            if (contextState is not null)
            {
                await contextState.EnsureLoadedAsync(cancellation.Token); if (disposed || version != loadVersion) return;
                if (contextState.Current is null) throw new InvalidOperationException(contextState.Error);
                key = loadedKey = AddressKey;
                if (!subscribed) { contextState.Changed += ContextChanged; subscribed = true; }
                if (!contextState.HasPermission("resources/read")) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            }
            EntryDraftResponse? incoming = null;
            if (!IsNew)
            {
                if (!string.IsNullOrWhiteSpace(RequestedOwner))
                {
                    if (!ResourceScopeRef.TryParse(RequestedOwner, out var owner)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
                    incoming = await Client.GetEntryAtAddressAsync(ScopedResourceAddress.Create(owner, EntryResourceNamespace, EntryResourceKinds.Entry, Name), cancellation.Token);
                }
                else incoming = await Client.GetEntryAsync(EntryResourceNamespace, Name, cancellation.Token);
                if (!Current(version, key)) return;
                if (!Matches(incoming, saved?.Value.WorkspaceId)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
                if (incoming.ManagedByPack && !IsPackOwned && HasDraft) { ClearPrivate(); Adopt(incoming); loadState = new(ResourceLoadStatus.Ready) { HasContent = true }; return; }
            }
            var availableWorkspaces = await Client.GetWorkspacesAsync(cancellation.Token); if (!Current(version, key)) return;
            if (incoming is not null && availableWorkspaces.All(value => value.Id != incoming.Value.WorkspaceId.Value)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            workspaces = availableWorkspaces;
            if (IsNew && baseline.Length == 0)
            {
                model.WorkspaceId = new(contextState?.Current?.WorkspaceId ?? workspaces.FirstOrDefault()?.Id ?? Guid.Empty); model.Namespace = EntryResourceNamespace.Value;
                model.Fields.Add(new() { Name = "request", Label = T("EntryRequestLabel"), Type = EntryFieldType.Prompt, Required = true, Primary = true, Validation = new(1, 10_000) });
            }
            if (incoming is not null)
            {
                if (HasDraft) newerEntry = JsonSerializer.Serialize(incoming) != JsonSerializer.Serialize(saved) ? incoming : null;
                else Adopt(incoming);
            }
            if (IsNew && baseline.Length == 0) baseline = Fingerprint;
            await LoadResourcesAsync(version, key); if (!Current(version, key)) return;
            await LoadUsagesAsync(version, key); if (!Current(version, key)) return;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = IsNew || saved is not null };
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!Current(version, key)) return;
            var failure = Failure(exception); if (failure.Status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearPrivate();
            loadState = failure with { HasContent = IsNew ? baseline.Length > 0 : saved is not null };
        }
        finally { if (Current(version, key)) loading = false; }
    }
    private bool Matches(EntryDraftResponse value, WorkspaceId? expected = null) => value.Value.Name == Name && value.Value.Id == new EntryId(Name, EntryResourceNamespace)
        && (string.IsNullOrWhiteSpace(RequestedOwner) || ResourceScopeRef.TryParse(RequestedOwner, out var owner) && owner == ResourceScopeRef.Workspace(value.Value.WorkspaceId.Value)) && value.Value.WorkspaceId.Value != Guid.Empty && (expected is null || value.Value.WorkspaceId == expected)
        && (contextState?.Current is null || value.Value.WorkspaceId.Value == contextState.Current.WorkspaceId)
        && (value.Published is null || value.Published.Id == value.Value.Id && value.Published.WorkspaceId == value.Value.WorkspaceId && value.Published.Name == value.Value.Name);
    private void Adopt(EntryDraftResponse incoming)
    {
        saved = incoming; newerEntry = null; model = EntryEditorModel.From(incoming.Value, BindingKey(incoming.Value.Binding, incoming.Value.Id.Namespace)); declaredBinding = incoming.Value.Binding; bindingEdited = invalidInput = false;
        ReplaceContext(); baseline = Fingerprint; rawError = null;
        if (!HasRawDraft) PrepareRawYaml();
    }
    private void ReplaceContext() { formContext.OnFieldChanged -= FieldChanged; formContext.OnValidationRequested -= ValidationRequested; formContext = new(model); messages = new(formContext); formContext.OnFieldChanged += FieldChanged; formContext.OnValidationRequested += ValidationRequested; }
    private void FieldChanged(object? sender, FieldChangedEventArgs args) { messages.Clear(); BridgeNestedErrors(); formContext.NotifyValidationStateChanged(); }
    private void ValidationRequested(object? sender, ValidationRequestedEventArgs args) => BridgeNestedErrors();
    private void BridgeNestedErrors()
    {
        if (model.Fields.Any(field => typeof(EntryEditorField).GetProperties().Any(property => formContext.GetValidationMessages(new FieldIdentifier(field, property.Name)).Any())))
            messages.Add(formContext.Field(nameof(model.Fields)), T("EntryFieldsInvalid"));
    }
    private void ClearPrivate()
    {
        saved = newerEntry = null; resources = []; dependencies = []; dashboards = []; workspaces = []; usageLoaded = false;
        model = new(); declaredBinding = new(EntryBindingKind.Agent, ""); bindingEdited = invalidInput = false; rawYaml = rawBaseline = baseline = message = ""; rawError = null; validation = null; resourceVersion++;
        ReplaceContext(); busy = loading = false;
    }
    private static ResourceLoadState Failure(Exception exception) => new(exception is AgentstrationApiException api ? api.StatusCode switch { System.Net.HttpStatusCode.NotFound => ResourceLoadStatus.NotFound, System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => ResourceLoadStatus.Forbidden, _ => ResourceLoadStatus.Failed } : ResourceLoadStatus.Failed) { ErrorId = (exception as AgentstrationApiException)?.ErrorId };
    private async Task LoadResourcesAsync(long version, string? key)
    {
        var request = ++resourceVersion; var kind = model.BindingKind; var available = await Client.GetResourcesAsync(kind, cancellation.Token);
        if (Current(version, key) && request == resourceVersion && kind == model.BindingKind) resources = available;
    }
    private async Task BindingKindChanged(ChangeEventArgs args) { if (busy || !CanWrite) return; model.BindingKind = Enum.Parse<EntryBindingKind>(args.Value?.ToString() ?? "Agent"); model.ResourceId = ""; bindingEdited = true; resources = []; await LoadResourcesAsync(loadVersion, loadedKey); }
    private void BindingSelectionChanged() => bindingEdited = true;
    private static string ResourceKey(ResourcePickerItem value) => value.ScopeRef is null ? $"{value.Namespace.Value}:{value.ResourceId}" : $"{value.ScopeRef.Value.Value}|{value.Namespace.Value}|{value.ResourceId}";
    private static string BindingKey(EntryBinding value, ResourceNamespace? ownerNamespace = null) => value.ScopeRef is null ? $"{(value.Namespace ?? ownerNamespace ?? ResourceNamespace.Default).Value}:{value.ResourceId}" : $"{value.ScopeRef.Value.Value}|{(value.Namespace ?? ResourceNamespace.Default).Value}|{value.ResourceId}";
    private EntryBinding CurrentBinding() => !bindingEdited ? declaredBinding : SelectedResource is { } value ? new(model.BindingKind, value.ResourceId, value.Namespace, value.ScopeRef) : new(model.BindingKind, model.ResourceId);
    private EntryDraft Draft() => model.Draft(CurrentBinding(), draftRevision, publishedBinding);
    private async Task LoadUsagesAsync(long version, string? key)
    {
        try
        {
            var values = new List<WorkplaceDashboardDraftResponse>();
            foreach (var workspace in workspaces)
            {
                var available = await Client.GetDashboardsAsync(workspace.Name, cancellation.Token); if (!Current(version, key)) return;
                values.AddRange(available);
            }
            IReadOnlyList<EntryDependencyResponse> refs = [];
            if (!IsNew) { refs = await Client.GetDependenciesAsync(EntryResourceNamespace, Name, cancellation.Token); if (!Current(version, key)) return; }
            dashboards = values; dependencies = refs; usageLoaded = true;
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version, key)) { usageLoaded = false; dashboards = []; dependencies = []; hasError = true; message = exception.Message; } }
    }
    private async Task<bool> ValidateFormAsync(bool fromYaml = false)
    {
        messages.Clear(); var valid = formContext.Validate() && !invalidInput;
        if (valid)
        {
            try { WorkplaceValidation.Validate(Draft()); }
            catch (WorkValidationException exception) { messages.Add(formContext.Field(EntryEditorModel.InvalidField(exception)!), Localizer[$"EntryValidation.{exception.Code}"].ResourceNotFound ? T("EntryDraftInvalid", exception.Code) : T($"EntryValidation.{exception.Code}")); valid = false; }
        }
        formContext.NotifyValidationStateChanged();
        if (!valid && !IsNew && !fromYaml) { revealValidation = true; ShowSection(ResourceDefinitionForm.FirstInvalidSection(formContext, Presentations) ?? sectionState.DefinitionPath); }
        if (!valid && IsNew && wizard is not null) { var destination = InvalidStep(); pendingValidationStep = destination; await wizard.GoToStepAsync(destination); }
        return valid;
    }
    private Task SaveAsync() => ExecuteCommandAsync(EntryCommand.Save);
    private Task ValidateAsync() => ExecuteCommandAsync(EntryCommand.Validate);
    private Task PublishAsync() => ExecuteCommandAsync(EntryCommand.Publish);
    private async Task ExecuteCommandAsync(EntryCommand command, bool fromYaml = false)
    {
        if (disposed || !CanWrite || busy || loading || HasRawDraft && !fromYaml) return;
        if (!await ValidateFormAsync(fromYaml)) { if (fromYaml) rawError = formContext.GetValidationMessages().FirstOrDefault() ?? T("EntryDraftInvalid", "entry_validation_invalid"); return; }
        var version = loadVersion; var key = loadedKey; var submitted = Draft(); var originalIdentity = saved?.Value.WorkspaceId;
        busy = true; hasError = false; message = "";
        try
        {
            var accepted = await Client.SaveEntryAsync(EntryResourceNamespace, submitted, cancellation.Token); if (!Current(version, key)) return;
            if (accepted.Id != submitted.Id || accepted.Name != submitted.Name || accepted.WorkspaceId != submitted.WorkspaceId || originalIdentity is { } original && accepted.WorkspaceId != original)
            { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            saved = new(accepted, published, IsPackOwned); newerEntry = null; model.Original = accepted; declaredBinding = accepted.Binding; bindingEdited = invalidInput = false; baseline = Fingerprint;
            if (fromYaml) { rawBaseline = rawYaml; }
            if (command is EntryCommand.Validate or EntryCommand.Publish)
            {
                var checkedDraft = await Client.ValidateEntryAsync(EntryResourceNamespace, accepted.Name, cancellation.Token); if (!Current(version, key)) return;
                validation = checkedDraft;
                if (!validation.IsValid) { hasError = true; message = T(command == EntryCommand.Publish ? "ResolveValidationErrors" : "ValidationFoundErrors"); return; }
                if (command == EntryCommand.Publish)
                {
                    var released = await Client.PublishEntryAsync(EntryResourceNamespace, accepted.Name, cancellation.Token); if (!Current(version, key)) return;
                    if (released.Id != accepted.Id || released.Name != accepted.Name || released.WorkspaceId != accepted.WorkspaceId) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
                    saved = saved with { Published = released, Value = saved.Value with { PublishedBinding = accepted.Binding } };
                    message = T("EntryPublished", released.Version, released.ResolvedTarget.Version);
                }
                else message = T("EntryValid");
            }
            else message = T("EntryDraftSaved", accepted.Revision);
            if (!HasRawDraft) PrepareRawYaml();
            await LoadUsagesAsync(version, key); if (!Current(version, key)) return;
            if (IsNew) Navigation.NavigateTo(EntryUrl(accepted));
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version, key)) { hasError = true; message = exception.Message; } }
        finally { if (Current(version, key)) busy = false; }
    }
    private string EntryUrl(EntryDraft value) => ResourceOwnershipContext.PreserveNavigation(value.Id.Namespace.IsDefault ? $"/entries/{Uri.EscapeDataString(value.Name)}" : $"/namespaces/{Uri.EscapeDataString(value.Id.Namespace.Value)}/entries/{Uri.EscapeDataString(value.Name)}", Services);
    private void Discard()
    {
        if (busy || loading || saved is null) return;
        rawYaml = rawBaseline = ""; Adopt(newerEntry ?? saved); message = ""; validation = null;
    }
    private void AddField() { if (!CanWrite || busy) return; model.Fields.Add(new() { Name = $"field{model.Fields.Count + 1}", Label = T("NewField"), Type = EntryFieldType.Text, Primary = model.Fields.Count == 0, Order = model.Fields.Count, Validation = new(1, 10_000) }); }
    private void RemoveField(EntryEditorField field) { if (!CanWrite || busy || model.Fields.Count <= 1) return; model.Fields.Remove(field); if (model.Fields.All(value => !value.Primary)) model.Fields[0].Primary = true; }
    private void SetPrimary(EntryEditorField field) { if (!CanWrite || busy) return; foreach (var value in model.Fields) value.Primary = ReferenceEquals(value, field); }
    private void MoveField(EntryEditorField field, int offset) { if (!CanWrite || busy) return; var index = model.Fields.IndexOf(field); var target = index + offset; if (index < 0 || target < 0 || target >= model.Fields.Count) return; model.Fields.RemoveAt(index); model.Fields.Insert(target, field); for (var i = 0; i < model.Fields.Count; i++) model.Fields[i].Order = i; }
    private void AddSuggestion() { if (CanWrite && !busy) model.Suggestions.Add(new() { Label = T("Suggestion"), Value = "" }); }
    private void RemoveSuggestion(EntryEditorSuggestion suggestion) { if (CanWrite && !busy) model.Suggestions.Remove(suggestion); }
    private string FormatDate(DateTimeOffset? value) => value is null || value == default ? T("NotPublished") : value.Value.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    private static string FriendlyResourceName(string value) => value[(value.LastIndexOf('/') + 1)..];
    private string FriendlyRelationship(string value) => value == "ResolvedTarget" ? T("ResolvedTarget") : value;
    private string FieldTitle(EntryEditorField value) => string.IsNullOrWhiteSpace(value.Label) ? string.IsNullOrWhiteSpace(value.Name) ? T("UntitledField") : value.Name : value.Label;
    private string BindingText(EntryBinding value, ResourceNamespace? ownerNamespace = null) => $"{EnumLabel("Binding", value.Kind)} · {(value.ScopeRef is { } owner ? owner.Value + " · " : "")}{value.Namespace ?? ownerNamespace ?? EntryResourceNamespace}/{value.ResourceId}";
    private static Task PreviewSubmitted(IReadOnlyDictionary<string, JsonElement> _) => Task.CompletedTask;
    private enum EntryCommand { Save, Validate, Publish }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!interactive) { interactive = true; StateHasChanged(); }
        if (!disposed && revealValidation && definitionForm is not null && !busy) { revealValidation = false; await definitionForm.RevealValidationAsync(); }
    }
    public void Dispose() { disposed = true; loadVersion++; resourceVersion++; if (subscribed && contextState is not null) contextState.Changed -= ContextChanged; formContext.OnFieldChanged -= FieldChanged; formContext.OnValidationRequested -= ValidationRequested; cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
