using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Secrets.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class SourceRegistries
{
    [Parameter] public string? Name { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? ContextValue { get; set; }
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken lifetimeToken;
    private readonly ResourceSectionState sectionState = new();
    private readonly string formId = $"source-registry-{Guid.NewGuid():N}";
    private ResourceSnapshot<SourceRegistryRegistrationView>? snapshot, newerSnapshot;
    private SourceRegistryRegistrationView? view;
    private SourceRegistryOriginTrustView? originTrust;
    private IReadOnlyList<SourceRegistryRefreshRecordResource>? refreshes;
    private IReadOnlyList<SecretResponse> instanceSecrets = [];
    private SourceRegistryEditorModel model = new();
    private EditContext editContext;
    private ValidationMessageStore messages;
    private ResourceDefinitionForm? definitionForm;
    private CreationWizard? wizard;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private AgentstrationApiException? error;
    private string? loadedKey, baseline, pendingNavigation, pendingValidationStep;
    private long loadVersion;
    private bool loading, commandBusy, invalidInput, completed, confirmDelete, disposed, subscribed, revealValidation;

    public SourceRegistries() { lifetimeToken = cancellation.Token; editContext = new(model); messages = new(editContext); }
    private bool IsNew => new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/').EndsWith("/new", StringComparison.OrdinalIgnoreCase);
    private bool IsList => string.IsNullOrWhiteSpace(Name) && !IsNew;
    private string ContextKey => $"{IsList}|{IsNew}|{Name}|{ContextValue}|{ContextState.Current?.UserId}|{ContextState.Current?.SelectedScopeRef}";
    private ResourceScopeRef? CurrentScope => ResourceOwnershipContext.TryGetCurrent(Services) ?? (ContextState.Current is { } current ? ResourceScopeRef.Parse(current.SelectedScopeRef) : null);
    private bool OwnScope => CurrentScope == ResourceScopeRef.Instance;
    private bool busy => loading || commandBusy;
    private bool CanWrite => !disposed && !completed && !loadState.BlocksContent && OwnScope && ContextState.HasPermission("resources/write") && view?.Registration.Governance.IsManaged != true;
    private bool CanRefresh => !disposed && OwnScope && ContextState.HasPermission("resources/write");
    private bool CanDelete => CanWrite && ContextState.HasPermission("resources/delete") && view is not null && view.Registration.Name != SourceRegistryWellKnown.OfficialName;
    private ResourceReadOnlyReason? ReadOnlyReason => view is null ? null : !OwnScope ? ResourceReadOnlyReason.Inherited : view.Registration.Governance.IsManaged ? ResourceReadOnlyReason.Managed : !ContextState.HasPermission("resources/write") ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private string Fingerprint => JsonSerializer.Serialize(model);
    private bool HasDraft => !completed && baseline is not null && (Fingerprint != baseline || invalidInput);
    private string BackUrl => ResourceOwnershipContext.PreserveNavigation("/settings/source-registries", Services);
    private string T(string key) => Localizer[key].Value;
    private string F(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private string SavedYaml => view is null ? string.Empty : Agentstration.ResourceManagement.ResourceManifestSerializer.ToYaml(view.Registration);
    private IReadOnlyList<ResourceNavigationItem> DefinitionSections => [new("definition/refresh", T("RefreshPolicy"), "clock")];
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections => [new("origin", T("OriginTrust"), "shield"), new("history", T("RefreshHistory"), "history")];
    private IReadOnlyDictionary<string, ResourceSectionPresentation> Presentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { HasRequiredFields = true, Fields = [nameof(model.Name), nameof(model.DisplayName), nameof(model.Namespace), nameof(model.ScopeRef), nameof(model.Description), nameof(model.Tags)], ReadOnlyActions = ReadOnlyDiscard },
        ["definition/configuration"] = new() { HasRequiredFields = true, Fields = [nameof(model.IndexUrl), nameof(model.Enabled), nameof(model.TrustPolicy), nameof(model.AuthenticationMode), nameof(model.CredentialName), nameof(model.CredentialNamespace), nameof(model.CredentialScopeRef), nameof(model.AllowHttp), nameof(model.AllowPrivateNetwork)], ReadOnlyActions = ReadOnlyDiscard },
        ["definition/refresh"] = new() { Fields = [nameof(model.PeriodicEnabled), nameof(model.IntervalMinutes), nameof(model.TimeoutSeconds), nameof(model.MaximumAttempts), nameof(model.InitialBackoffSeconds), nameof(model.MaximumBackoffSeconds), nameof(model.JitterSeconds), nameof(model.StaleAfterMinutes), nameof(model.RetainedObservations)], ReadOnlyActions = ReadOnlyDiscard },
        ["yaml"] = new() { ReadOnly = true }
    };
    private RenderFragment? ReadOnlyDiscard => !HasDraft ? null : builder => { builder.OpenElement(0, "button"); builder.AddAttribute(1, "type", "button"); builder.AddAttribute(2, "class", "button-secondary"); builder.AddAttribute(3, "disabled", busy); builder.AddAttribute(4, "onclick", EventCallback.Factory.Create(this, DiscardAsync)); builder.AddContent(5, Shared["Discard"].Value); builder.CloseElement(); };
    private IReadOnlyDictionary<string, string> FieldLabels => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(model.Name)] = Shared["TechnicalName"],
        [nameof(model.DisplayName)] = T("DisplayName"),
        [nameof(model.Namespace)] = Shared["Namespace"],
        [nameof(model.ScopeRef)] = Shared["Scope"],
        [nameof(model.Description)] = Shared["Description"],
        [nameof(model.Tags)] = Shared["Labels"],
        [nameof(model.IndexUrl)] = T("IndexUrl"),
        [nameof(model.CredentialName)] = T("CredentialReference"),
        [nameof(model.IntervalMinutes)] = T("IntervalMinutes"),
        [nameof(model.TimeoutSeconds)] = T("TimeoutSeconds"),
        [nameof(model.MaximumAttempts)] = T("MaximumAttempts"),
        [nameof(model.InitialBackoffSeconds)] = T("InitialBackoffSeconds"),
        [nameof(model.MaximumBackoffSeconds)] = T("MaximumBackoffSeconds"),
        [nameof(model.JitterSeconds)] = T("JitterSeconds"),
        [nameof(model.StaleAfterMinutes)] = T("StaleAfterMinutes"),
        [nameof(model.RetainedObservations)] = T("RetainedObservations")
    };
    private IReadOnlyList<CreationWizardStep> Steps => [new("identity", Shared["Identity"], IdentityFields) { IsComplete = Complete("definition/identity") }, new("configuration", Shared["Configuration"], ConfigurationFields) { IsComplete = Complete("definition/configuration") }, new("refresh", T("RefreshPolicy"), RefreshFields) { IsComplete = Complete("definition/refresh") }];
    private bool Complete(string path) => Presentations[path].Fields.All(field => !editContext.GetValidationMessages(editContext.Field(field)).Any() && Validator.TryValidateProperty(typeof(SourceRegistryEditorModel).GetProperty(field)!.GetValue(model), new ValidationContext(model) { MemberName = field }, null)) && (path != "definition/configuration" || model.HasRequiredCredential);
    private Task ShowSection(string path) { Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path); return Task.CompletedTask; }
    private string InvalidStep() => (ResourceDefinitionForm.FirstInvalidSection(editContext, Presentations) ?? "definition/configuration").Split('/')[1];
    private async Task InvalidSubmitAsync(EditContext context) { if (IsNew && wizard is not null) await wizard.GoToStepAsync(InvalidStep()); definitionForm?.RequestValidationFocus(); }
    private Task StepReadyAsync(string step) { if (disposed || pendingValidationStep != step || definitionForm is null) return Task.CompletedTask; pendingValidationStep = null; definitionForm.RequestValidationFocus(); return InvokeAsync(StateHasChanged); }
    private bool ValidateDraft()
    {
        messages.Clear(); var valid = editContext.Validate();
        if (!model.HasRequiredCredential) { messages.Add(editContext.Field(nameof(model.CredentialName)), T("SelectCredential")); editContext.NotifyValidationStateChanged(); valid = false; }
        return valid && !invalidInput;
    }
    private async Task FinishAsync(CreationWizardFinish finish) { if (busy || !CanWrite) return; if (!ValidateDraft()) { finish.ReturnToStepId = pendingValidationStep = InvalidStep(); return; } await SaveAsync(); if (editContext.GetValidationMessages().Any()) finish.ReturnToStepId = pendingValidationStep = InvalidStep(); }
    protected override async Task OnParametersSetAsync()
    {
        await ContextState.EnsureLoadedAsync(lifetimeToken); if (disposed) return;
        if (!subscribed) { ContextState.Changed += ContextChanged; subscribed = true; }
        if (loadedKey == ContextKey) return;
        loadedKey = ContextKey; ClearPrivate(); sectionState.Reset(loadedKey); if (!IsList) await LoadAsync();
    }
    private void ContextChanged() => _ = InvokeAsync(async () => { if (disposed) return; if (loadedKey != ContextKey) { loadedKey = ContextKey; ++loadVersion; ClearPrivate(); sectionState.Reset(loadedKey); if (!IsList) await LoadAsync(); } else if (!ContextState.HasPermission("resources/read")) { ++loadVersion; ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); } StateHasChanged(); });
    private bool Current(long request, string? key) => !disposed && request == loadVersion && loadedKey == key && key == ContextKey;
    private void ResetDraft() { model = view is null ? new() : SourceRegistryEditorModel.From(view.Registration); editContext = new(model); messages = new(editContext); invalidInput = false; baseline = Fingerprint; }
    private void ClearPrivate() { view = null; snapshot = newerSnapshot = null; refreshes = null; originTrust = null; instanceSecrets = []; error = null; completed = confirmDelete = commandBusy = false; pendingNavigation = null; pendingValidationStep = null; ResetDraft(); baseline = null; }
    private static ResourceLoadStatus FailureStatus(Exception exception) => exception is AgentstrationApiException api ? api.StatusCode switch { HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden, HttpStatusCode.NotFound => ResourceLoadStatus.NotFound, _ => ResourceLoadStatus.Failed } : ResourceLoadStatus.Failed;
    private bool Matches(ResourceSnapshot<SourceRegistryRegistrationView> incoming) => incoming.Value.Registration is var resource && resource.Uid != Guid.Empty && resource.Kind == SourceRegistryKinds.SourceRegistryRegistration && resource.ScopeRef == ResourceScopeRef.Instance && resource.Namespace == ResourceNamespace.Default && (IsNew ? resource.Name == model.Name.Trim() : resource.Name == Name) && (snapshot is null || resource.Uid == snapshot.Value.Registration.Uid && resource.Name == snapshot.Value.Registration.Name) && incoming.Value.Observed.Definition.RegistrationUid == resource.Uid && incoming.Value.Observed.ScopeRef == ResourceScopeRef.Instance;
    private Task RefreshAsync() => busy || IsList ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync(bool discard = false)
    {
        var request = ++loadVersion; var key = loadedKey; loading = true; var hadContent = view is not null || IsNew && baseline is not null;
        loadState = new(hadContent ? ResourceLoadStatus.Refreshing : ResourceLoadStatus.Loading) { HasContent = hadContent };
        try
        {
            if (!ContextState.HasPermission("resources/read")) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            if (!IsNew)
            {
                var incoming = await Client.GetRegistryAsync(Name!, lifetimeToken); if (!Current(request, key)) return;
                if (!Matches(incoming)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
                if (!discard && HasDraft) { newerSnapshot = snapshot?.ETag != incoming.ETag ? incoming : null; }
                else Adopt(incoming);
            }
            else if (baseline is null) ResetDraft();
            await LoadSecretsAsync(request, key); if (!Current(request, key)) return;
            if (!IsNew) await LoadDetailsAsync(request, key); if (!Current(request, key)) return;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException)
        {
            if (!Current(request, key)) return; var status = FailureStatus(exception); if (status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearPrivate();
            loadState = new(status) { HasContent = view is not null || IsNew && baseline is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
        finally { if (Current(request, key)) loading = false; }
    }
    private void Adopt(ResourceSnapshot<SourceRegistryRegistrationView> incoming) { snapshot = incoming; view = incoming.Value; newerSnapshot = null; ResetDraft(); }
    private async Task LoadSecretsAsync(long request, string? key)
    {
        try { var choices = await Secrets.GetSecretsAsync(lifetimeToken); if (Current(request, key)) instanceSecrets = choices.Where(value => value.Resource.ScopeRef == ResourceScopeRef.Instance).OrderBy(value => value.Resource.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
        catch (AgentstrationApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) { if (Current(request, key)) instanceSecrets = []; }
    }
    private async Task LoadDetailsAsync(long request, string? key)
    {
        if (view is null) return; var uid = view.Registration.Uid; var name = view.Registration.Name;
        try
        {
            var historyTask = Client.GetRefreshesAsync(name, lifetimeToken); var trustTask = Client.GetOriginTrustAsync(name, lifetimeToken); await Task.WhenAll(historyTask, trustTask); if (!Current(request, key)) return;
            var history = await historyTask; var trust = await trustTask;
            if (history.Any(value => value.Definition.RegistrationUid != uid) || trust.RegistrationUid != uid || trust.RegistrationName != name) { refreshes = null; originTrust = null; throw new AgentstrationApiException(T("Editor.DetailsUnavailable"), Guid.NewGuid().ToString("N")); }
            refreshes = history; originTrust = trust;
        }
        catch (AgentstrationApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) { if (Current(request, key)) { ClearPrivate(); loadState = new(FailureStatus(exception)); } throw; }
    }
    private async Task SaveAsync()
    {
        if (busy || !CanWrite || !ValidateDraft()) { revealValidation = true; return; }
        var request = loadVersion; var key = loadedKey; commandBusy = true; error = null;
        try
        {
            var properties = model.ToProperties();
            var incoming = IsNew ? await Client.CreateRegistryAsync(new(model.Name.Trim(), properties), lifetimeToken) : await Client.UpdateRegistryAsync(Name!, new(properties), snapshot!.ETag, lifetimeToken);
            if (!Current(request, key)) return;
            if (!Matches(incoming)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            if (IsNew) { completed = true; baseline = null; Notify("RegistryCreated", incoming.Value.Registration.Definition.DisplayName); pendingNavigation = RegistryUrl(incoming.Value.Registration.Name); }
            else { Adopt(incoming); Notify("RegistryUpdated", view!.Registration.Definition.DisplayName); await LoadDetailsAsync(request, key); }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException or ArgumentException or OverflowException) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = false; }
    }
    private async Task ToggleAsync(bool value)
    {
        if (busy || !CanWrite) return; if (IsNew) { model.Enabled = value; return; }
        if (snapshot is null) return;
        var request = loadVersion; var key = loadedKey; commandBusy = true; error = null;
        try
        {
            var incoming = await Client.UpdateRegistryAsync(Name!, new(snapshot.Value.Registration.Definition with { Enabled = value }), snapshot.ETag, lifetimeToken); if (!Current(request, key)) return;
            if (!Matches(incoming)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            snapshot = incoming; view = incoming.Value; newerSnapshot = null; model.Enabled = view.Registration.Definition.Enabled; baseline = JsonSerializer.Serialize(SourceRegistryEditorModel.From(view.Registration));
            Notify("RegistryUpdated", view.Registration.Definition.DisplayName);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = false; }
    }
    private async Task RefreshCurrentAsync()
    {
        if (busy || !CanRefresh || snapshot is null || !snapshot.Value.Registration.Definition.Enabled) return;
        var request = loadVersion; var key = loadedKey; commandBusy = true; error = null; var followUp = false;
        try
        {
            var incoming = await Client.RefreshRegistryAsync(Name!, lifetimeToken); if (!Current(request, key)) return;
            if (!Matches(incoming)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            if (HasDraft && snapshot.ETag != incoming.ETag) newerSnapshot = incoming;
            else if (HasDraft) { snapshot = incoming; view = incoming.Value; }
            else Adopt(incoming);
            await LoadDetailsAsync(request, key); if (Current(request, key)) Notify("RegistryRefreshed", incoming.Value.Registration.Definition.DisplayName);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(request, key)) { SetError(exception); followUp = true; } }
        finally { if (Current(request, key)) commandBusy = false; }
        if (followUp && Current(request, key)) await LoadAsync();
    }
    private async Task DiscardAsync() { if (busy) return; if (newerSnapshot is { } newer) Adopt(newer); else ResetDraft(); error = null; await Task.CompletedTask; }
    private Task ReloadAfterConflictAsync() => busy ? Task.CompletedTask : LoadAsync(discard: true);
    private async Task DeleteAsync()
    {
        if (busy || !CanDelete || snapshot is null) return; var request = loadVersion; var key = loadedKey; confirmDelete = false; commandBusy = true; error = null;
        try { await Client.DeleteRegistryAsync(Name!, snapshot.ETag, lifetimeToken); if (!Current(request, key)) return; ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); pendingNavigation = BackUrl; }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or TimeoutException) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = false; }
    }
    private void SetError(Exception exception) => error = exception as AgentstrationApiException ?? new(T("Editor.OperationUnconfirmed"), Guid.NewGuid().ToString("N"));
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        if (pendingNavigation is { } target) { pendingNavigation = null; Navigation.NavigateTo(target); return; }
        if (revealValidation && !busy && definitionForm is { Busy: false }) { revealValidation = false; await definitionForm.RevealValidationAsync(); }
    }
    private string CredentialKey => model.CredentialName.Length == 0 ? string.Empty : ReferenceKey(model.CredentialName, model.CredentialScopeRef, model.CredentialNamespace);
    private static string ReferenceKey(string name, ResourceScopeRef? scope, ResourceNamespace? ns) => $"{scope?.Value ?? "?"}|{ns?.Value ?? "?"}:{name}";
    private static string ReferenceDisplay(string name, ResourceScopeRef? scope, ResourceNamespace? ns) => $"{scope?.Value ?? "—"} · {(ns ?? ResourceNamespace.Default).Value}/{name}";
    private static string SecretKey(SecretResponse value) => ReferenceKey(value.Resource.Name, value.Resource.ScopeRef, value.Resource.Namespace);
    private void SelectCredential(string value)
    {
        if (busy || !CanWrite) return; if (value.Length == 0) model.CredentialName = string.Empty;
        else if (instanceSecrets.FirstOrDefault(secret => SecretKey(secret) == value) is { } choice) { model.CredentialName = choice.Resource.Name; model.CredentialNamespace = choice.Resource.Namespace; model.CredentialScopeRef = choice.Resource.ScopeRef; }
        messages.Clear(editContext.Field(nameof(model.CredentialName))); editContext.NotifyFieldChanged(editContext.Field(nameof(model.CredentialName)));
    }
    private IReadOnlyList<Setting> SavedSettings
    {
        get
        {
            if (view is null) return []; var definition = view.Registration.Definition; var refresh = definition.RefreshPolicy;
            return [new(T("IndexUrl"), definition.IndexUrl.AbsoluteUri), new(T("Enabled"), BooleanLabel(definition.Enabled)), new(T("TrustPolicy"), TrustPolicy(definition.TrustPolicy)), new(T("Authentication"), T($"Authentication.{definition.AuthenticationMode}")), new(T("CredentialReference"), definition.Credential is { } credential ? ReferenceDisplay(credential.Name, credential.ScopeRef, credential.Namespace) : "—"), new(T("AllowHttp"), BooleanLabel(definition.EndpointPolicy.AllowHttp)), new(T("AllowPrivateNetwork"), BooleanLabel(definition.EndpointPolicy.AllowPrivateNetwork)), new(T("PeriodicRefresh"), BooleanLabel(refresh.PeriodicEnabled)), new(T("IntervalMinutes"), refresh.Interval.TotalMinutes.ToString(CultureInfo.CurrentCulture)), new(T("TimeoutSeconds"), refresh.Timeout.TotalSeconds.ToString(CultureInfo.CurrentCulture)), new(T("MaximumAttempts"), refresh.MaximumAttempts.ToString(CultureInfo.CurrentCulture)), new(T("InitialBackoffSeconds"), refresh.InitialBackoff.TotalSeconds.ToString(CultureInfo.CurrentCulture)), new(T("MaximumBackoffSeconds"), refresh.MaximumBackoff.TotalSeconds.ToString(CultureInfo.CurrentCulture)), new(T("JitterSeconds"), refresh.Jitter.TotalSeconds.ToString(CultureInfo.CurrentCulture)), new(T("StaleAfterMinutes"), refresh.StaleAfter.TotalMinutes.ToString(CultureInfo.CurrentCulture)), new(T("RetainedObservations"), definition.CachePolicy.RetainedObservations.ToString(CultureInfo.CurrentCulture))];
        }
    }
    private void Notify(string titleKey, string message) => Notifications.Add(new(Guid.NewGuid(), T(titleKey), message, Clock.GetLocalNow(), UiStatus.Success));
    private string ObservedStatus(SourceRegistryObservedStatus value) => Enum.IsDefined(value) ? T($"Observed.{value}") : T("Editor.UnknownState");
    private string RefreshOutcome(SourceRegistryRefreshOutcome? value) => value is null ? T("Never") : T($"Outcome.{value}");
    private string RefreshTrigger(SourceRegistryRefreshTrigger value) => T($"Trigger.{value}");
    private string TrustPolicy(SourceRegistryTrustPolicy value) => T($"TrustPolicy.{value}");
    private string OriginClassification(SourceRegistryOriginClassification value) => T($"Origin.{value}");
    private string BooleanLabel(bool value) => T(value ? "Yes" : "No");
    private static UiStatus ObservedTone(SourceRegistryObservedStatus value) => value switch { SourceRegistryObservedStatus.Fresh or SourceRegistryObservedStatus.Recovered => UiStatus.Success, SourceRegistryObservedStatus.NeverFetched or SourceRegistryObservedStatus.Disabled => UiStatus.Neutral, SourceRegistryObservedStatus.Stale or SourceRegistryObservedStatus.NoCompatibleCatalog => UiStatus.Warning, _ => Enum.IsDefined(value) ? UiStatus.Danger : UiStatus.Neutral };
    private string FormatDate(DateTimeOffset? value) => value is null ? "—" : value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    private string RegistryUrl(string name) => ResourceOwnershipContext.PreserveNavigation($"/settings/source-registries/{Uri.EscapeDataString(name)}", Services);
    public void Dispose() { if (disposed) return; disposed = true; ++loadVersion; if (subscribed) ContextState.Changed -= ContextChanged; cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
