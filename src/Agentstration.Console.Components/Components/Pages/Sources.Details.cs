using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class Sources
{
    private readonly ResourceSectionState sectionState = new();
    private SourceEditorModel model = new();
    private EditContext? detailContext;
    private EditContext DetailContext => detailContext ??= CreateDetailContext();
    private ResourceDefinitionForm? definitionForm;
    private ValidationMessageStore? policyMessages;
    private readonly HashSet<FieldIdentifier> touchedPolicyFields = [];
    private bool policiesSubmitted, invalidInput, detailLoading, disposed, detailInitialized, installedPacksKnown;
    private long detailRevision, packRevision, detailEpoch;
    private string detailKey = "pending";
    private string? identityBaseline, refreshBaseline, bindingsBaseline;
    private SourceConsoleDetailView? newerDetail;
    private Func<SourceConsoleDetailView, bool>? pendingReceipt;
    private ResourceLoadState detailLoadState = new(ResourceLoadStatus.Loading);
    private string DetailKey => $"{Publisher}|{Name}|{ScopeRefValue}|{VersionUid}|{CurrentContextKey}";
    private bool OwnScope => detail?.Source.Source.ScopeRef is { } scope && ResourceOwnershipContext.CanManage(scope, Services);
    private bool CanWrite => !disposed && detail is not null && OwnScope && !detail.Source.Source.Governance.IsManaged && !detail.Source.Configuration.Governance.IsManaged && ContextState.HasPermission("resources/write");
    private bool CanDelete => !disposed && detail is not null && OwnScope && !detail.Source.Source.Governance.IsManaged && ContextState.HasPermission("resources/delete");
    private bool CanRefresh => !disposed && detail is not null && OwnScope && ContextState.HasPermission("resources/write");
    private bool CanInstall => !disposed && detail is not null && ContextState.HasPermission("resources/write");
    private bool DetailBusy => busy || detailLoading || deletingSource;
    private ResourceReadOnlyReason? DetailReadOnlyReason => detail?.Source.Configuration.Governance.IsManaged == true ? ResourceReadOnlyReason.Managed : !CanWrite ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private bool HasDefinitionDraft => identityBaseline is not null && (identityBaseline != model.DisplayName || refreshBaseline != RefreshFingerprint || bindingsBaseline != BindingsFingerprint || invalidInput || pendingReceipt is not null);
    private string RefreshFingerprint => JsonSerializer.Serialize(new { model.SourceRefresh, model.ChannelRefresh, Overrides = model.ChannelOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal) });
    private string BindingsFingerprint => JsonSerializer.Serialize(model.BindingSelections.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    private string SourceCollectionUrl => ResourceOwnershipContext.PreserveNavigation("/settings/sources", Services);
    private IReadOnlyDictionary<string, ResourceSectionPresentation> DetailPresentations => new Dictionary<string, ResourceSectionPresentation>
    {
        ["overview"] = new(),
        ["definition/identity"] = new() { HasRequiredFields = true, Fields = [nameof(model.DisplayName), nameof(model.Name), nameof(model.Namespace), nameof(model.ScopeRef), nameof(model.Description), nameof(model.Tags)] },
        ["definition/configuration"] = new() { Fields = [nameof(model.BindingSelections)] },
        ["definition/refresh"] = new() { Fields = [nameof(model.SourceRefresh), nameof(model.ChannelRefresh), nameof(model.ChannelOverrides)] },
        ["content"] = new(),
        ["versions"] = new(),
        ["yaml"] = new() { ReadOnly = true }
    };
    private IReadOnlyDictionary<string, string> DetailFieldLabels => new Dictionary<string, string> { [nameof(model.DisplayName)] = T("DisplayName") };
    private IReadOnlyList<ResourceNavigationItem> DetailDefinitionSections => [new("definition/refresh", T("RefreshScheduling"), "clock")];
    private IReadOnlyList<ResourceNavigationItem> DetailAdditionalSections => [new("content", T("ChannelsAndCatalogs"), "database"), new("versions", T("Versions"), "history")];
    private RenderFragment DetailSectionContent(string path) => path switch { "definition/identity" => IdentityContent, "definition/refresh" => RefreshContent, _ => BindingContent };
    private void ShowSection(string path) => Navigation.NavigateTo(new UriBuilder(Navigation.Uri) { Fragment = path }.Uri.AbsoluteUri);
    private sealed record SourceSetting(string Label, string Value);
    private IReadOnlyList<SourceSetting> SavedSourceSettings
    {
        get
        {
            if (detail is null) return [];
            var values = new List<SourceSetting>
            {
                new(T("Publisher"), detail.Source.Source.Definition.Publisher), new(T("SelectedVersion"), detail.SelectedVersion.Definition.Version),
                new(T("Origin"), detail.Source.Configuration.Definition.Origin?.Url ?? T("PastedManifest")),
                new(T("SourceDefinitionRefresh"), PolicySummary(detail.Source.Configuration.Definition.Refresh.Source)),
                new(T("ChannelRefreshDefaults"), PolicySummary(detail.Source.Configuration.Definition.Refresh.Channels))
            };
            values.AddRange(detail.Source.Configuration.Definition.Refresh.ChannelOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new SourceSetting(F("ChannelRefreshTarget", pair.Key), PolicySummary(pair.Value))));
            values.AddRange(detail.Source.Configuration.Definition.Bindings.Select(binding => new SourceSetting($"{T("Bindings")} · {binding.Name}", ReferenceText(binding.Target))));
            values.AddRange(detail.SelectedVersion.Definition.PublishedDefinition.Channels.Select(channel => new SourceSetting($"{T("Channel")} · {channel.Name}", F("ChannelContract", channel.Provider.Binding, channel.Configuration.OptionSet, channel.Configuration.Version) + Environment.NewLine + (channel.Configuration.Values.ValueKind == JsonValueKind.Undefined ? "—" : channel.Configuration.Values.GetRawText()))));
            return values;
        }
    }
    private string PolicySummary(SourceRefreshPolicy policy) => F("PolicySummary", T(policy.Enabled ? "Enabled" : "Disabled"), policy.IntervalSeconds, policy.TimeoutSeconds, policy.MaximumAttempts, policy.InitialBackoffSeconds, policy.MaximumBackoffSeconds, policy.JitterSeconds);
    private static string ReferenceText(ResourceReference value) => $"{value.ScopeRef?.Value} · {value.Namespace?.Value ?? ResourceNamespace.DefaultValue}/{value.Name}";
    private static bool EqualJson<T>(T left, T right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left), JsonSerializer.SerializeToNode(right));

    private EditContext CreateDetailContext()
    {
        var context = new EditContext(model); policyMessages = new(context); touchedPolicyFields.Clear(); policiesSubmitted = false;
        context.OnFieldChanged += DetailFieldChanged; context.OnValidationRequested += ValidatePolicies; return context;
    }
    private void ResetDefinition()
    {
        if (detailContext is not null) { detailContext.OnFieldChanged -= DetailFieldChanged; detailContext.OnValidationRequested -= ValidatePolicies; }
        model = detail is null ? new() : SourceEditorModel.From(detail); detailContext = CreateDetailContext(); invalidInput = false;
        identityBaseline = detail is null ? null : model.DisplayName; refreshBaseline = RefreshFingerprint; bindingsBaseline = BindingsFingerprint;
    }
    private void DetailFieldChanged(object? sender, FieldChangedEventArgs args)
    {
        if (args.FieldIdentifier.Model is SourceRefreshPolicyForm) touchedPolicyFields.Add(args.FieldIdentifier);
        ValidatePolicyFields();
    }
    private void ValidatePolicies(object? sender, ValidationRequestedEventArgs args) { policiesSubmitted = true; ValidatePolicyFields(); }
    private void ValidatePolicyFields()
    {
        policyMessages!.Clear();
        foreach (var problem in model.ValidatePolicies())
        {
            var field = new FieldIdentifier(problem.Model, problem.Field);
            if (!policiesSubmitted && !touchedPolicyFields.Contains(field)) continue;
            var message = problem.MessageKey == "Validation.Range" ? Shared[problem.MessageKey, T(problem.Field), problem.Minimum!, problem.Maximum!].Value : T(problem.MessageKey);
            policyMessages.Add(field, message);
            // Root section metadata routes nested policy failures; the field renders the message once.
            if (policiesSubmitted) policyMessages.Add(DetailContext.Field(problem.RootField), message);
        }
        if (policiesSubmitted)
        {
            foreach (var (policy, root) in new[] { (model.SourceRefresh, nameof(model.SourceRefresh)), (model.ChannelRefresh, nameof(model.ChannelRefresh)) }
                .Concat(model.ChannelOverrides.Values.Where(value => value.Custom).Select(value => (value, nameof(model.ChannelOverrides)))))
                foreach (var field in new[] { nameof(policy.IntervalMinutes), nameof(policy.TimeoutSeconds), nameof(policy.MaximumAttempts), nameof(policy.InitialBackoffSeconds), nameof(policy.MaximumBackoffSeconds), nameof(policy.JitterSeconds) })
                    foreach (var message in DetailContext.GetValidationMessages(new FieldIdentifier(policy, field)).ToArray())
                        policyMessages.Add(DetailContext.Field(root), message);
        }
        DetailContext.NotifyValidationStateChanged();
    }
    private Task InvalidDefinitionSubmitAsync(EditContext context)
    {
        if (ResourceDefinitionForm.FirstInvalidSection(context, DetailPresentations) is null && context.GetValidationMessages().Any()) { ShowSection("definition/refresh"); definitionForm?.RequestValidationFocus(); }
        return Task.CompletedTask;
    }
    protected override async Task OnParametersSetAsync()
    {
        if (IsCollection) { if (detailInitialized) ClearDetail(); await LoadCollectionAsync(); return; }
        if (detailInitialized && detailKey == DetailKey) return;
        detailInitialized = true; detailKey = DetailKey; detailRevision++; packRevision++; busy = detailLoading = false; ClearDetail(); sectionState.Reset(detailKey);
        await ReadDetailAsync();
    }
    private void DetailContextChanged()
    {
        if (disposed || !detailInitialized || IsCollection) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed || IsCollection) return;
            if (detailKey != DetailKey) { detailKey = DetailKey; detailRevision++; packRevision++; busy = detailLoading = false; ClearDetail(); sectionState.Reset(detailKey); }
            await ReadDetailAsync(); if (!disposed) StateHasChanged();
        });
    }
    private void ClearDetail()
    {
        detailEpoch++; busy = false;
        detail = newerDetail = null; pendingReceipt = null; installedPackIdentities = new HashSet<string>(StringComparer.Ordinal); installedPacksKnown = false;
        selectedChannelName = null; confirmDeleteSource = deletingSource = false; deleteError = error = null;
        packSelection = null; sourcePackPreview = null; packBindingSelections.Clear(); packModelProfiles = []; packModelProviders = []; packRuntimeProfiles = []; packExtensions = []; packParameters = []; packSecrets = [];
        packError = null; packPreviewing = packInstalling = false; ResetDefinition();
    }
    private bool CurrentCommand(long epoch, string key) => !disposed && !IsCollection && epoch == detailEpoch && detailKey == key && key == DetailKey;
    private bool CurrentDetail(long request, string key) => !disposed && !IsCollection && request == detailRevision && detailKey == key && key == DetailKey;
    private void EnsureDetailIdentity(SourceConsoleDetailView incoming)
    {
        var resource = incoming.Source.Source;
        if (resource.Uid == Guid.Empty || resource.Kind != SourceResourceKinds.Source || resource.ScopeRef != ResourceScopeRef.Parse(ScopeRefValue!)
            || resource.Name != Name || resource.Namespace.Value != Publisher || resource.Definition.Publisher != Publisher
            || detail is not null && resource.Uid != detail.Source.Source.Uid
            || incoming.Source.Configuration.Definition.SourceUid != resource.Uid || incoming.Source.Configuration.ScopeRef != resource.ScopeRef || incoming.Source.Configuration.Name != resource.Name || incoming.Source.Configuration.Namespace != resource.Namespace
            || detail is not null && incoming.Source.Configuration.Uid != detail.Source.Configuration.Uid
            || incoming.Source.Observed.Definition.SourceUid != resource.Uid || incoming.Source.Observed.ScopeRef != resource.ScopeRef
            || incoming.SelectedVersion.Definition.SourceUid != resource.Uid || incoming.SelectedVersion.ScopeRef != resource.ScopeRef || incoming.SelectedVersion.Definition.SourceName != resource.Name || incoming.SelectedVersion.Definition.Publisher != Publisher
            || VersionUid is { } version && incoming.SelectedVersion.Uid != version
            || incoming.Versions.Any(value => value.Definition.SourceUid != resource.Uid || value.ScopeRef != resource.ScopeRef)
            || incoming.Bindings.SourceVersionUid != incoming.SelectedVersion.Uid
            || incoming.Channels.Any(value => value.CurrentSnapshot is { } snapshot && (snapshot.Definition.SourceUid != resource.Uid || snapshot.Definition.SourceVersionUid != incoming.SelectedVersion.Uid)))
            throw new DetailIdentityException();
    }
    private sealed class DetailIdentityException : Exception { }
    private static ResourceLoadStatus DetailFailure(Exception exception)
    {
        var status = exception is AgentstrationApiException api ? api.StatusCode : (exception as HttpRequestException)?.StatusCode;
        return exception is AuthorizationDeniedException || status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? ResourceLoadStatus.Forbidden
            : exception is DetailIdentityException || status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Failed;
    }
    private Task RefreshDetailAsync() => DetailBusy ? Task.CompletedTask : ReadDetailAsync();
    private async Task ReadDetailAsync(bool discard = false)
    {
        if (disposed || IsCollection) return;
        var request = ++detailRevision; var key = detailKey; detailLoading = true;
        detailLoadState = new(detail is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = detail is not null };
        try
        {
            var incoming = await Service.GetAsync(ResourceScopeRef.Parse(ScopeRefValue!), Publisher!, Name!, VersionUid, catalogLocale, lifetime.Token);
            if (!CurrentDetail(request, key)) return; EnsureDetailIdentity(incoming);
            if (!discard && HasDefinitionDraft)
            {
                if (pendingReceipt is not null && pendingReceipt(incoming)) { detail = incoming; pendingReceipt = null; newerDetail = null; }
                else if (detail!.Source.Configuration.ETag != incoming.Source.Configuration.ETag || detail.Source.Source.ETag != incoming.Source.Source.ETag || detail.SelectedVersion.Uid != incoming.SelectedVersion.Uid) newerDetail = incoming;
                else detail = incoming;
            }
            else { detail = incoming; newerDetail = null; pendingReceipt = null; ResetDefinition(); }
            if (!detail!.Channels.Any(channel => channel.Definition.Name == selectedChannelName)) selectedChannelName = detail.Channels.FirstOrDefault()?.Definition.Name;
            try
            {
                var packs = await Services.GetRequiredService<IPacksClient>().GetPacksAsync(lifetime.Token);
                if (!CurrentDetail(request, key)) return;
                installedPackIdentities = packs.Select(value => PackIdentityKey(value.Definition.Publisher, value.Definition.PackName)).ToHashSet(StringComparer.Ordinal); installedPacksKnown = true;
            }
            catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException or OperationCanceledException)
            { if (CurrentDetail(request, key)) installedPacksKnown = false; }
            if (CurrentDetail(request, key)) detailLoadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!CurrentDetail(request, key)) return; var status = DetailFailure(exception);
            if (status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearDetail();
            detailLoadState = new(status) { HasContent = detail is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
        finally { if (CurrentDetail(request, key)) detailLoading = false; }
    }
    private async Task DiscardDefinitionAsync()
    {
        if (DetailBusy) return;
        if (newerDetail is { } latest) { detail = latest; newerDetail = null; pendingReceipt = null; ResetDefinition(); detailLoadState = new(ResourceLoadStatus.Ready) { HasContent = true }; }
        else { pendingReceipt = null; await ReadDetailAsync(discard: true); }
    }
    private async Task SaveDefinitionAsync()
    {
        if (DetailBusy || !CanWrite || pendingReceipt is not null || !DetailContext.Validate()) return;
        var submittedDisplayName = model.DisplayName;
        var identityChanged = identityBaseline != submittedDisplayName;
        var submittedRefresh = model.ToRefreshConfiguration();
        var submittedRefreshFingerprint = RefreshFingerprint;
        var refreshChanged = refreshBaseline != submittedRefreshFingerprint;
        var submittedBindings = model.BindingSelections.Values.ToArray();
        var submittedBindingsFingerprint = BindingsFingerprint;
        var bindingsChanged = bindingsBaseline != submittedBindingsFingerprint;
        var submittedVersionUid = detail!.SelectedVersion.Uid;
        var key = detailKey; var epoch = detailEpoch; detailRevision++; busy = true; error = null;
        try
        {
            if (identityChanged)
            {
                var expected = detail!.Source.Configuration.Definition with { DisplayName = submittedDisplayName.Trim() };
                await Service.UpdateDisplayNameAsync(ScopeRef(), Publisher!, Name!, submittedDisplayName, detail.Source.Configuration.ETag!, lifetime.Token);
                if (!CurrentCommand(epoch, key)) return;
                identityBaseline = submittedDisplayName; await ConfirmConfigurationWriteAsync(expected, key, submittedVersionUid);
                if (!CurrentCommand(epoch, key) || pendingReceipt is not null || newerDetail is not null) return;
            }
            if (refreshChanged)
            {
                var expected = detail!.Source.Configuration.Definition with { Refresh = submittedRefresh };
                await Service.UpdateRefreshConfigurationAsync(ScopeRef(), Publisher!, Name!, submittedRefresh, detail.Source.Configuration.ETag!, lifetime.Token);
                if (!CurrentCommand(epoch, key)) return;
                refreshBaseline = submittedRefreshFingerprint; await ConfirmConfigurationWriteAsync(expected, key, submittedVersionUid);
                if (!CurrentCommand(epoch, key) || pendingReceipt is not null || newerDetail is not null) return;
            }
            if (bindingsChanged)
            {
                var previous = detail!.Source.Configuration.Definition;
                await Service.ConfigureBindingsAsync(ScopeRef(), Publisher!, Name!, submittedVersionUid, submittedBindings, detail.Bindings.ConfigurationETag, lifetime.Token);
                if (!CurrentCommand(epoch, key)) return;
                bindingsBaseline = submittedBindingsFingerprint;
                pendingReceipt = incoming => incoming.SelectedVersion.Uid == submittedVersionUid && MatchesBindingsWrite(incoming, previous, submittedBindings);
                await ReadDetailAsync();
                if (!CurrentCommand(epoch, key) || pendingReceipt is not null || newerDetail is not null) return;
            }
            if (CurrentCommand(epoch, key)) Notify("SourceUpdated", T("DefinitionSaved"));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (CurrentCommand(epoch, key)) error = exception.Message; }
        finally { if (CurrentCommand(epoch, key)) busy = false; }
    }
    private async Task ConfirmConfigurationWriteAsync(SourceConfigurationProperties expected, string key, Guid submittedVersionUid)
    {
        // The command returns no envelope. A read may rebase the ETag only after confirming all unchanged settings.
        detail = detail! with { Source = detail.Source with { Configuration = detail.Source.Configuration with { Definition = expected } } };
        pendingReceipt = incoming => incoming.SelectedVersion.Uid == submittedVersionUid && EqualJson(incoming.Source.Configuration.Definition, expected);
        await ReadDetailAsync();
        if (detailKey != key || disposed) return;
    }
    private static bool MatchesBindingsWrite(SourceConsoleDetailView incoming, SourceConfigurationProperties previous, IReadOnlyList<SourceConsoleBindingSelection> selections)
    {
        var current = incoming.Source.Configuration.Definition;
        if (current.DisplayName != previous.DisplayName || !EqualJson(current.Origin, previous.Origin) || !EqualJson(current.Refresh, previous.Refresh)) return false;
        foreach (var binding in incoming.Bindings.Bindings)
        {
            var selection = selections.SingleOrDefault(value => value.Name == binding.Name && value.TargetKind == binding.TargetKind);
            if (selection is null) { if (binding.Target is not null) return false; continue; }
            if (selection.Target is { } target) { if (target != binding.Target) return false; }
            else if (selection.Candidate is { } candidate)
            {
                var provider = incoming.Providers.SingleOrDefault(value => value.Value.Name == binding.Target?.Name && value.Value.Namespace == binding.Target?.Namespace && value.Value.ScopeRef == binding.Target?.ScopeRef)?.Value;
                if (provider is null || provider.Definition.ContributionId != candidate.ContributionId || provider.Definition.Extension.Name != candidate.RegistrationName
                    || provider.Definition.Extension.Namespace != candidate.RegistrationNamespace || provider.Definition.Extension.ScopeRef != candidate.RegistrationScopeRef) return false;
            }
            else return false;
        }
        return true;
    }
    private async Task RefreshChannelAsync(string channel) => await RunSourceCommandAsync(() => Service.RefreshChannelAsync(ScopeRef(), Publisher!, Name!, detail!.SelectedVersion.Uid, channel, lifetime.Token), "ChannelRefreshed", F("ChannelRefreshedMessage", channel));
    private async Task RefreshSourceAsync()
    {
        if (detail?.Source.Configuration.Definition.Origin is null) return;
        await RunSourceCommandAsync(async () => { _ = await Service.RefreshSourceAsync(ScopeRef(), Publisher!, Name!, lifetime.Token); }, "SourceRefreshed", T("SourceRefreshFinished"));
    }
    private async Task RunSourceCommandAsync(Func<Task> action, string title, string message)
    {
        if (DetailBusy || !CanRefresh) return; var key = detailKey; var epoch = detailEpoch; busy = true; error = null;
        try { await action(); if (CurrentCommand(epoch, key)) Notify(title, message); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (CurrentCommand(epoch, key)) error = exception.Message; }
        finally { if (CurrentCommand(epoch, key)) { await ReadDetailAsync(); if (CurrentCommand(epoch, key)) busy = false; } }
    }
    private async Task DeleteSourceAsync()
    {
        if (DetailBusy || !CanDelete) return; var key = detailKey; var epoch = detailEpoch; deletingSource = true; deleteError = null;
        try
        {
            var source = detail!.Source.Source;
            await Service.DeleteSourceAsync(source.ScopeRef!.Value, source.Definition.Publisher, source.Name, source.ETag!, lifetime.Token);
            if (!CurrentCommand(epoch, key)) return;
            Notify("SourceDeleted", F("SourceDeletedMessage", detail.Source.Configuration.Definition.DisplayName)); ClearDetail(); Navigation.NavigateTo(SourceCollectionUrl);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (CurrentCommand(epoch, key)) deleteError = exception.Message; }
        finally { if (CurrentCommand(epoch, key)) deletingSource = false; }
    }
    private void CloseDeleteSource() { if (!deletingSource) { confirmDeleteSource = false; deleteError = null; } }

    private SourceImportEditorModel importModel = new();
    private EditContext? importContext;
    private EditContext ImportContext => importContext ??= new(importModel);
    private ResourceDefinitionForm? importDefinitionForm;
    private CreationWizard? importWizard;
    private bool importAttempted, importScopeInitialized;
    private ResourceScopeRef? initialImportScope;
    private long importEpoch;
    private bool HasImportDraft => importing && (!string.IsNullOrWhiteSpace(importUrl) || !string.IsNullOrWhiteSpace(importYaml) || importScopeInitialized && importScope != initialImportScope);
    private void ImportScopeChanged(ResourceScopeRef? value)
    {
        if (!importScopeInitialized) { initialImportScope = value; importScopeInitialized = true; }
        importScope = value;
    }
    private void ResetImportDraft()
    {
        importEpoch++; importing = false; importAttempted = importScopeInitialized = false; initialImportScope = null; importModel = new(); importContext = null;
    }
    private IReadOnlyDictionary<string, string> ImportFieldLabels => new Dictionary<string, string>();
    private IReadOnlyList<CreationWizardStep> ImportSteps => [new("manifest", T("ImportManifest"), ImportManifestContent) { IsComplete = importModel.HasManifest }, new("destination", T("ImportDestination"), ImportDestinationContent) { IsComplete = importScope is not null }, new("review", T("ImportReview"), ImportReviewContent) { IsComplete = CanImport }];
    private async Task FinishImportAsync(CreationWizardFinish finish)
    {
        importAttempted = true;
        if (!CanImport) { finish.ReturnToStepId = !importModel.HasManifest ? "manifest" : "destination"; importDefinitionForm?.RequestValidationFocus(); return; }
        await ImportAsync();
    }
    private Task ImportStepReady(string _) { if (importAttempted) importDefinitionForm?.RequestValidationFocus(); return Task.CompletedTask; }
    private void CloseImport() { if (!busy) ResetImportDraft(); }
    private async Task ImportAsync()
    {
        if (busy || !CanImport || !hasCollectionAuthorization) return; var key = CurrentContextKey; var epoch = importEpoch; busy = true; error = null;
        try
        {
            var result = importMode == "url" ? await Service.ImportUrlAsync(new Uri(importUrl), importScope!.Value, lifetime.Token) : await Service.ImportYamlAsync(importYaml, importScope!.Value, lifetime.Token);
            if (disposed || !IsCollection || key != CurrentContextKey || epoch != importEpoch) return;
            Notify("ImportComplete", F("ImportCompleteMessage", result.Source.Configuration.Definition.DisplayName, result.Version.Definition.Version)); importing = false;
            Navigation.NavigateTo(ResourceOwnershipContext.PreserveNavigation(SourceUrl(result.Source), Services));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!disposed && key == CurrentContextKey && IsCollection && epoch == importEpoch) error = exception.Message; }
        finally { if (!disposed && key == CurrentContextKey && epoch == importEpoch) busy = false; }
    }
}
