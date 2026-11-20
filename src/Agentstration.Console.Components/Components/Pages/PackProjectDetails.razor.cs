using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Extensions.Contracts;
using Agentstration.Models.Contracts;
using Agentstration.Packs.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Contracts;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Pages;

public partial class PackProjectDetails
{
    [Parameter] public Guid ProjectId { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? ContextValue { get; set; }
    private static readonly JsonSerializerOptions PublicJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken lifetimeToken;
    private readonly ResourceSectionState sectionState = new();
    private readonly string formId = $"pack-project-{Guid.NewGuid():N}";
    private readonly Dictionary<string, SourceEntry> sourceEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, InstallationState> installationDrafts = [];
    private readonly Dictionary<string, string> bindingSelections = new(StringComparer.Ordinal);
    private ResourceSnapshot<PackProjectResource>? project;
    private IReadOnlyList<PackProjectBuildResource>? builds;
    private IReadOnlyList<PackProjectSourceDocument>? sourceDocuments;
    private DetailBundle? newerBundle;
    private PackProjectEditorModel model = new();
    private EditContext metadataContext;
    private ValidationMessageStore validation;
    private ResourceDefinitionForm? definitionForm;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? loadedKey, baseline, activeSourcePath, message, installationBaseline;
    private bool loading, commandBusy, building, installing, hasError, invalidMetadata, revealMetadataErrors, disposed;
    private bool replaceExisting, installationSavedReplace, previewVerified;
    private Dictionary<string, string> installationSavedBindings = new(StringComparer.Ordinal);
    private long loadVersion;
    private PackProjectBuildResource? selectedBuild;
    private PackInstallationPreview? preview;
    private IReadOnlyList<ModelProfileSummaryResponse> modelProfiles = [];
    private IReadOnlyList<ModelProviderResponse> modelProviders = [];
    private IReadOnlyList<RuntimeProfileSummaryResponse> runtimeProfiles = [];
    private IReadOnlyList<ExtensionRegistrationResource> extensionRegistrations = [];
    private IReadOnlyList<SecretResponse> secrets = [];
    private IReadOnlyList<ParameterResource> parameters = [];

    public PackProjectDetails() { lifetimeToken = cancellation.Token; metadataContext = new(model); validation = new(metadataContext); }
    private string ContextKey => $"{ProjectId:D}|{ContextValue}|{ContextState.Current?.UserId}|{ContextState.Current?.SelectedScopeRef}|{ContextState.Current?.WorkspaceId}";
    private string Identity => $"{loadedKey}|{project?.Value.Uid}|{project?.Value.ScopeRef}|{project?.Value.Namespace}|{project?.Value.Name}";
    private string Fingerprint => JsonSerializer.Serialize(model);
    private bool HasMetadataDraft => baseline is not null && (Fingerprint != baseline || invalidMetadata);
    private bool HasSourceDraft => sourceEditors.Values.Any(entry => entry.Component?.IsDirty == true);
    private bool HasVersionDraft => HasMetadataDraft || HasSourceDraft;
    private bool HasAdditionalDraft => HasSourceDraft || InstallationDirty || installationDrafts.Values.Any(value => value.Build.Uid != selectedBuild?.Uid && value.IsDirty);
    private bool busy => loading || commandBusy;
    private ResourceScopeRef? CurrentScope => ResourceOwnershipContext.TryGetCurrent(Services) ?? (ContextState.Current is { } current ? ResourceScopeRef.Parse(current.SelectedScopeRef) : null);
    private bool OwnScope => project?.Value.ScopeRef is { } owner && owner == CurrentScope;
    private bool CanAuthor => !disposed && OwnScope && project?.Value.Governance.IsManaged == false && ContextState.HasPermission("resources/write");
    private bool CanInstallPermission => !disposed && ContextState.HasPermission("resources/write");
    private ResourceReadOnlyReason? ReadOnlyReason => project is null ? null : !OwnScope ? ResourceReadOnlyReason.Inherited : project.Value.Governance.IsManaged ? ResourceReadOnlyReason.Managed : !ContextState.HasPermission("resources/write") ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private string Title => project?.Value.Definition.DisplayName ?? project?.Value.Definition.PackName ?? T("PackProject");
    private UiStatus ProjectTone => project is null || !Enum.IsDefined(project.Value.Definition.State) ? UiStatus.Neutral : project.Value.Definition.State == PackProjectState.Built ? UiStatus.Success : UiStatus.Info;
    private string ProjectStatus => project is null || !Enum.IsDefined(project.Value.Definition.State) ? T("Project.UnknownState") : T($"ProjectState.{project.Value.Definition.State}");
    private string BackUrl => ResourceOwnershipContext.PreserveNavigation("/packs", Services);
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections => [new("sources", T("PackResources"), "files"), new("builds", T("Builds"), "history"), new("installation", T("LocalDeployment"), "package")];
    private IReadOnlyDictionary<string, ResourceSectionPresentation> Presentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { Fields = [nameof(model.ResourceName), nameof(model.Namespace), nameof(model.ScopeRef), nameof(model.MetadataTags), nameof(model.DisplayName), nameof(model.Description), nameof(model.Publisher), nameof(model.PackName)], ReadOnlyActions = DiscardReadOnlyMetadata },
        ["definition/configuration"] = new() { HasRequiredFields = true, Fields = [nameof(model.Version), nameof(model.Audience), nameof(model.Purpose), nameof(model.Categories), nameof(model.ManifestTags)], ReadOnlyActions = DiscardReadOnlyMetadata },
        ["yaml"] = new() { ReadOnly = true }
    };
    private RenderFragment? DiscardReadOnlyMetadata => !HasMetadataDraft ? null : builder =>
    {
        builder.OpenElement(0, "button"); builder.AddAttribute(1, "type", "button"); builder.AddAttribute(2, "class", "button-secondary"); builder.AddAttribute(3, "disabled", busy); builder.AddAttribute(4, "onclick", EventCallback.Factory.Create(this, DiscardMetadataAsync)); builder.AddContent(5, Shared["Discard"].Value); builder.CloseElement();
    };
    private IReadOnlyDictionary<string, string> FieldLabels => new Dictionary<string, string> { [nameof(model.ResourceName)] = T("TechnicalName"), [nameof(model.Namespace)] = T("Namespace"), [nameof(model.ScopeRef)] = T("Scope"), [nameof(model.MetadataTags)] = Shared["Labels"], [nameof(model.Publisher)] = T("Publisher"), [nameof(model.PackName)] = T("TechnicalName"), [nameof(model.Version)] = T("Version"), [nameof(model.DisplayName)] = T("DisplayName"), [nameof(model.Description)] = T("Description"), [nameof(model.Audience)] = T("Audience"), [nameof(model.Purpose)] = T("Purpose"), [nameof(model.Categories)] = T("Categories"), [nameof(model.ManifestTags)] = T("Tags") };
    private Task ShowSection(string path) { Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path); return Task.CompletedTask; }
    protected override Task OnParametersSetAsync()
    {
        if (loadedKey == ContextKey) return Task.CompletedTask;
        loadedKey = ContextKey; ClearPrivate(); sectionState.Reset(loadedKey); return LoadAsync(false);
    }
    private void ResetMetadata(PackProjectResource resource) { model = PackProjectEditorModel.From(resource); metadataContext = new(model); validation = new(metadataContext); baseline = Fingerprint; invalidMetadata = false; }
    private void ClearPrivate()
    {
        project = null; newerBundle = null; builds = null; sourceDocuments = null; sourceEditors.Clear(); activeSourcePath = null; installationDrafts.Clear(); bindingSelections.Clear();
        selectedBuild = null; preview = null; modelProfiles = []; modelProviders = []; runtimeProfiles = []; extensionRegistrations = []; secrets = []; parameters = [];
        model = new(); metadataContext = new(model); validation = new(metadataContext); baseline = null; invalidMetadata = false; message = null; installationBaseline = null; replaceExisting = false; installationSavedBindings.Clear(); installationSavedReplace = previewVerified = false; commandBusy = building = installing = hasError = false;
    }
    private bool Current(long request, string? key) => !disposed && request == loadVersion && key == loadedKey && key == ContextKey;
    private bool Matches(ResourceSnapshot<PackProjectResource> snapshot, ResourceSnapshot<PackProjectResource>? previous) => snapshot.Value.Uid == ProjectId && snapshot.Value.Uid != Guid.Empty && snapshot.Value.Kind == PackAuthoringKinds.PackProject && snapshot.Value.ScopeRef is not null && !string.IsNullOrWhiteSpace(snapshot.Value.Name) && (previous is null || snapshot.Value.ScopeRef == previous.Value.ScopeRef && snapshot.Value.Namespace == previous.Value.Namespace && snapshot.Value.Name == previous.Value.Name);
    private bool MatchesBuild(PackProjectBuildResource build) => project is not null && build.Uid != Guid.Empty && build.Kind == PackAuthoringKinds.PackProjectBuild && build.Definition.ProjectId == ProjectId && build.ScopeRef is not null;
    private static ResourceLoadStatus FailureStatus(Exception exception) => exception is AgentstrationApiException api ? api.StatusCode switch { HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ResourceLoadStatus.Forbidden, HttpStatusCode.NotFound => ResourceLoadStatus.NotFound, _ => ResourceLoadStatus.Failed } : ResourceLoadStatus.Failed;
    private Task RefreshAsync() => busy ? Task.CompletedTask : LoadAsync(project is not null);
    private async Task LoadAsync(bool refresh)
    {
        var request = ++loadVersion; var key = loadedKey; var previous = project; loading = true;
        loadState = new(refresh && project is not null ? ResourceLoadStatus.Refreshing : ResourceLoadStatus.Loading) { HasContent = project is not null };
        try
        {
            await ContextState.EnsureLoadedAsync(lifetimeToken); if (disposed || request != loadVersion) return; loadedKey = key = ContextKey;
            if (!ContextState.HasPermission("resources/read")) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            var incoming = await Client.GetProjectAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return;
            if (!Matches(incoming, previous)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            IReadOnlyList<PackProjectBuildResource>? incomingBuilds = builds; IReadOnlyList<PackProjectSourceDocument>? incomingSources = sourceDocuments; AgentstrationApiException? auxiliaryError = null;
            try { incomingBuilds = await Client.GetBuildsAsync(ProjectId, lifetimeToken); }
            catch (AgentstrationApiException exception) when (FailureStatus(exception) == ResourceLoadStatus.Failed) { auxiliaryError = exception; }
            if (!Current(request, key)) return;
            try { incomingSources = await Client.GetProjectResourcesAsync(ProjectId, lifetimeToken); }
            catch (AgentstrationApiException exception) when (FailureStatus(exception) == ResourceLoadStatus.Failed) { auxiliaryError = exception; }
            if (!Current(request, key)) return;
            if (incomingBuilds?.Any(value => value.Uid == Guid.Empty || value.Kind != PackAuthoringKinds.PackProjectBuild || value.ScopeRef is null || value.Definition.ProjectId != ProjectId) == true) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            var bundle = new DetailBundle(incoming, incomingBuilds, incomingSources);
            if (HasVersionDraft) { builds = incomingBuilds; newerBundle = incoming.ETag != project?.ETag || !SameSources(incomingSources, sourceDocuments) ? bundle : null; }
            else Adopt(bundle);
            loadState = auxiliaryError is null ? new(ResourceLoadStatus.Ready) { HasContent = true } : new(ResourceLoadStatus.Failed) { HasContent = true, ErrorId = auxiliaryError.ErrorId };
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) { if (!Current(request, key)) return; var status = FailureStatus(exception); if (status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearPrivate(); loadState = new(status) { HasContent = project is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId }; }
        finally { if (Current(request, key)) loading = false; }
    }
    private static bool SameSources(IReadOnlyList<PackProjectSourceDocument>? left, IReadOnlyList<PackProjectSourceDocument>? right) => left is null ? right is null : right is not null && left.SequenceEqual(right);
    private void Adopt(DetailBundle bundle)
    {
        var metadataChanged = project is null || project.ETag != bundle.Snapshot.ETag; project = bundle.Snapshot; builds = bundle.Builds; sourceDocuments = bundle.Sources; newerBundle = null;
        if (metadataChanged || baseline is null) ResetMetadata(project.Value);
        foreach (var entry in sourceEditors.Values)
            if (sourceDocuments?.FirstOrDefault(value => value.Path == entry.Document.Path) is { } document) { entry.Document = document; if (entry.Component?.IsDirty != true) entry.ETag = project.ETag; }
    }
    private async Task DiscardMetadataAsync()
    {
        if (busy || project is null) return;
        if (newerBundle is { } latest && !HasSourceDraft) Adopt(latest); else ResetMetadata(project.Value);
        message = null; await Task.CompletedTask;
    }
    private async Task DiscardResourceDraftsAsync()
    {
        if (busy || newerBundle is not { } latest) return;
        foreach (var entry in sourceEditors.Values.ToArray())
        {
            if (latest.Sources?.FirstOrDefault(value => value.Path == entry.Document.Path) is { } document) { entry.Document = document; entry.ETag = latest.Snapshot.ETag; if (entry.Component is { } editor) await editor.AcknowledgeAsync(document); }
            else sourceEditors.Remove(entry.Document.Path);
        }
        Adopt(latest); ResetMetadata(latest.Snapshot.Value); message = null;
    }
    private async Task SelectSourceAsync(PackProjectSourceDocument document, bool useForm)
    {
        if (project is null || sourceDocuments?.Contains(document) != true) return;
        if (!sourceEditors.TryGetValue(document.Path, out var entry)) { entry = new(document, project.ETag, useForm); sourceEditors.Add(document.Path, entry); }
        activeSourcePath = document.Path; if (entry.Component is not null) await entry.Component.ShowModeAsync(useForm);
    }
    private void SourceDirtyChanged(string identity) { if (!disposed && identity == Identity) StateHasChanged(); }
    private void AcknowledgeEnvelope(ResourceSnapshot<PackProjectResource> incoming, bool metadataSaved, bool rebaseSources)
    {
        var previousEtag = project?.ETag; project = incoming; newerBundle = null;
        if (metadataSaved) ResetMetadata(incoming.Value);
        if (rebaseSources) foreach (var entry in sourceEditors.Values.Where(value => value.ETag == previousEtag)) entry.ETag = incoming.ETag;
        loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
    }
    private async Task SaveSourceAsync(string identity, UpdatePackProjectSourceCommand command)
    {
        if (busy || !CanAuthor || project is null || identity != Identity || !sourceEditors.TryGetValue(command.Path, out var entry)) return;
        var request = loadVersion; var key = loadedKey; var previous = project; commandBusy = true; message = null;
        try
        {
            var saved = await Client.UpdateProjectResourceAsync(ProjectId, command, entry.ETag, lifetimeToken); if (!Current(request, key)) return;
            if (!Matches(saved, previous)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            AcknowledgeEnvelope(saved, metadataSaved: false, rebaseSources: true);
            var accepted = entry.Document with { Source = command.Source }; entry.Document = accepted; entry.ETag = saved.ETag;
            if (sourceDocuments is not null) sourceDocuments = sourceDocuments.Select(value => value.Path == accepted.Path ? accepted : value).ToArray();
            if (entry.Component is not null) await entry.Component.AcknowledgeAsync(accepted);
            if (!Current(request, key)) return;
            var documents = await Client.GetProjectResourcesAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return; sourceDocuments = documents;
            foreach (var retained in sourceEditors.Values) if (documents.FirstOrDefault(value => value.Path == retained.Document.Path) is { } document) retained.Document = document;
            builds = await Client.GetBuildsAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return;
            hasError = false; message = T("Project.SourceSaved", accepted.Kind, accepted.Name, saved.Value.Definition.Revision);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = false; }
    }
    private async Task SaveAsync()
    {
        if (busy || !CanAuthor || project is null) return;
        commandBusy = true; try { await SaveMetadataCoreAsync(loadVersion, loadedKey); } finally { if (!disposed) commandBusy = false; }
    }
    private async Task<bool> SaveMetadataCoreAsync(long request, string? key)
    {
        if (project is null || !CanAuthor || invalidMetadata || !metadataContext.Validate()) { revealMetadataErrors = true; return false; }
        var previous = project; message = null;
        try
        {
            var saved = await Client.UpdateProjectAsync(ProjectId, model.Command(), previous.ETag, lifetimeToken); if (!Current(request, key)) return false;
            if (!Matches(saved, previous)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return false; }
            AcknowledgeEnvelope(saved, metadataSaved: true, rebaseSources: true); hasError = false; message = T("ProjectSaved"); return true;
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { return false; }
        catch (Exception exception) { if (Current(request, key)) SetError(exception); return false; }
    }
    private async Task BuildAsync()
    {
        if (busy || !CanAuthor || project is null) return; var request = loadVersion; var key = loadedKey; commandBusy = building = true;
        try
        {
            if (HasMetadataDraft && !await SaveMetadataCoreAsync(request, key)) return;
            if (!Current(request, key) || project is null) return; var previous = project;
            var built = await Client.BuildAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return;
            if (!MatchesBuild(built)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            builds = await Client.GetBuildsAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return;
            var incoming = await Client.GetProjectAsync(ProjectId, lifetimeToken); if (!Current(request, key)) return;
            if (!Matches(incoming, previous)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            var sameDefinition = JsonSerializer.Serialize(PackProjectEditorModel.From(previous.Value)) == JsonSerializer.Serialize(PackProjectEditorModel.From(incoming.Value)) && previous.Value.Definition.SourceArtifact.Sha256 == incoming.Value.Definition.SourceArtifact.Sha256;
            AcknowledgeEnvelope(incoming, metadataSaved: false, rebaseSources: sameDefinition); await SetPreviewAsync(built, request, key);
            if (!Current(request, key)) return; hasError = false; message = PreviewMessage(built.Definition.Version); await ShowSection("installation");
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = building = false; }
    }
    private bool InstallationDirty => installationBaseline is not null && InstallationFingerprint != installationBaseline;
    private string InstallationFingerprint => JsonSerializer.Serialize(new { replaceExisting, bindings = bindingSelections.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray() });
    private void RememberInstallation() { if (selectedBuild is not null) installationDrafts[selectedBuild.Uid] = new(selectedBuild, preview, new(bindingSelections, StringComparer.Ordinal), replaceExisting, installationBaseline, new(installationSavedBindings, StringComparer.Ordinal), installationSavedReplace, InstallationDirty); }
    private void AcceptInstallation() { installationBaseline = InstallationFingerprint; installationSavedBindings = new(bindingSelections, StringComparer.Ordinal); installationSavedReplace = replaceExisting; }
    private void DiscardInstallation() { if (busy) return; bindingSelections.Clear(); foreach (var pair in installationSavedBindings) bindingSelections[pair.Key] = pair.Value; replaceExisting = installationSavedReplace; RememberInstallation(); }
    private async Task SetPreviewAsync(PackProjectBuildResource build, long request, string? key)
    {
        RememberInstallation(); previewVerified = false; selectedBuild = build; preview = null; bindingSelections.Clear(); replaceExisting = false; installationBaseline = null;
        var retained = installationDrafts.GetValueOrDefault(build.Uid);
        if (retained is not null) { preview = retained.Preview; foreach (var pair in retained.Bindings) bindingSelections[pair.Key] = pair.Value; replaceExisting = retained.Replace; installationBaseline = retained.Baseline; installationSavedBindings = new(retained.SavedBindings, StringComparer.Ordinal); installationSavedReplace = retained.SavedReplace; }
        var incoming = await Client.PreviewBuildAsync(ProjectId, build.Uid, lifetimeToken); if (!Current(request, key)) return;
        preview = incoming; await ConfigureBindingsAsync(request, key, retained is not null); if (!Current(request, key)) return;
        if (retained is null || !retained.IsDirty) AcceptInstallation();
        previewVerified = true;
        RememberInstallation();
    }
    private async Task PreviewAsync(PackProjectBuildResource build)
    {
        if (busy || !MatchesBuild(build)) return; var request = loadVersion; var key = loadedKey; commandBusy = true;
        try { await SetPreviewAsync(build, request, key); if (Current(request, key)) { hasError = false; message = PreviewMessage(build.Definition.Version); await ShowSection("installation"); } }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = false; }
    }
    private bool CanInstall => previewVerified && CanInstallPermission && preview is not null && (preview.CanInstall || replaceExisting && preview.AlreadyInstalled) && preview.Bindings.All(binding => !binding.Required || !string.IsNullOrWhiteSpace(SelectedBinding(binding.Name)));
    private string PreviewStatus => preview is null ? T("NotPreviewed") : preview.Bindings.Any(binding => binding.Required && string.IsNullOrWhiteSpace(SelectedBinding(binding.Name))) ? T("ConfigurationRequired") : preview.CanInstall ? T("Ready") : preview.AlreadyInstalled ? T("AlreadyInstalled") : T("Conflicts");
    private async Task InstallAsync()
    {
        if (busy || !CanInstall || selectedBuild is null) return; var request = loadVersion; var key = loadedKey; var build = selectedBuild; commandBusy = installing = true;
        try
        {
            var bindings = preview!.Bindings.Where(binding => !string.IsNullOrWhiteSpace(SelectedBinding(binding.Name))).Select(binding => new PackBindingSelection(binding.Name, ParseBindingTarget(SelectedBinding(binding.Name)!))).ToArray();
            var installed = await Client.InstallBuildAsync(ProjectId, build.Uid, replaceExisting, bindings, lifetimeToken); if (!Current(request, key)) return;
            preview = await Client.PreviewBuildAsync(ProjectId, build.Uid, lifetimeToken); if (!Current(request, key)) return;
            await ConfigureBindingsAsync(request, key, preserve: true); if (!Current(request, key)) return; replaceExisting = false; AcceptInstallation(); RememberInstallation(); hasError = false; message = T("PackInstalled", installed.Value.Definition.Publisher, installed.Value.Definition.PackName);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) SetError(exception); }
        finally { if (Current(request, key)) commandBusy = installing = false; }
    }
    private async Task ConfigureBindingsAsync(long request, string? key, bool preserve)
    {
        if (preview is null) return;
        foreach (var binding in preview.Bindings) if ((!preserve || !bindingSelections.ContainsKey(binding.Name)) && binding.TargetAvailable && binding.Target is not null) bindingSelections[binding.Name] = BindingValue(binding.Target.Name, binding.Target.Namespace ?? ResourceNamespace.Default, binding.Target.ScopeRef);
        foreach (var kind in preview.Bindings.Select(value => value.TargetKind).Distinct())
        {
            try
            {
                switch (kind)
                {
                    case PackBindingTargetKind.ModelProfile: var profiles = await Services.GetRequiredService<IModelProfilesClient>().GetModelProfilesAsync(null, null, null, lifetimeToken); if (Current(request, key)) modelProfiles = profiles; break;
                    case PackBindingTargetKind.ModelProvider: var providers = await Services.GetRequiredService<IModelProvidersClient>().GetModelProvidersAsync(lifetimeToken); if (Current(request, key)) modelProviders = providers; break;
                    case PackBindingTargetKind.RuntimeProfile: var runtimes = await Services.GetRequiredService<IRuntimeProfilesClient>().GetRuntimeProfilesAsync(lifetimeToken); if (Current(request, key)) runtimeProfiles = runtimes; break;
                    case PackBindingTargetKind.ExtensionRegistration: var extensions = await Services.GetRequiredService<IExtensionsClient>().GetRegistrationsAsync(lifetimeToken); if (Current(request, key)) extensionRegistrations = extensions; break;
                    case PackBindingTargetKind.Secret: var secretChoices = await Services.GetRequiredService<ISecretsClient>().GetSecretsAsync(lifetimeToken); if (Current(request, key)) secrets = secretChoices; break;
                    case PackBindingTargetKind.Parameter: var parameterChoices = await Services.GetRequiredService<IParametersClient>().GetParametersAsync(lifetimeToken); if (Current(request, key)) parameters = parameterChoices; break;
                }
            }
            catch (AgentstrationApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                if (!Current(request, key)) return;
                switch (kind) { case PackBindingTargetKind.ModelProfile: modelProfiles = []; break; case PackBindingTargetKind.ModelProvider: modelProviders = []; break; case PackBindingTargetKind.RuntimeProfile: runtimeProfiles = []; break; case PackBindingTargetKind.ExtensionRegistration: extensionRegistrations = []; break; case PackBindingTargetKind.Secret: secrets = []; break; case PackBindingTargetKind.Parameter: parameters = []; break; }
            }
            if (!Current(request, key)) return;
        }
    }
    private IEnumerable<string> BindingOptions(PackBindingTargetKind kind) => kind switch
    {
        PackBindingTargetKind.ModelProfile => modelProfiles.Select(value => BindingValue(value.Name, ResourceNamespace.Parse(value.Namespace), value.ScopeRef)),
        PackBindingTargetKind.ModelProvider => modelProviders.Select(value => BindingValue(value.Name, ResourceNamespace.Parse(value.Namespace), value.ScopeRef)),
        PackBindingTargetKind.RuntimeProfile => runtimeProfiles.Select(value => BindingValue(value.Name, ResourceNamespace.Parse(value.Namespace), value.ScopeRef)),
        PackBindingTargetKind.ExtensionRegistration => extensionRegistrations.Select(value => BindingValue(value.Name, value.Namespace, value.ScopeRef)),
        PackBindingTargetKind.Parameter => parameters.Select(BindingValue),
        _ => secrets.Select(value => BindingValue(value.Resource.Name, value.Resource.Namespace, value.Resource.ScopeRef))
    };
    private string? SelectedBinding(string name) => bindingSelections.GetValueOrDefault(name);
    private void BindingChanged(string name, ChangeEventArgs args) { if (!commandBusy && CanInstallPermission) { bindingSelections[name] = args.Value?.ToString() ?? string.Empty; RememberInstallation(); } }
    private void SetError(Exception exception) { hasError = true; message = exception is HttpRequestException or TimeoutException ? T("Project.OperationUnconfirmed") : exception.Message; }
    private string PublicYaml
    {
        get
        {
            if (project is null) return string.Empty;
            var definition = JsonSerializer.SerializeToNode(project.Value.Definition, PublicJsonOptions)!.AsObject();
            if (definition["sourceArtifact"] is JsonObject artifact) artifact.Remove("storageKey");
            return ResourceManifestSerializer.ToYaml(new { project.Value.ApiVersion, project.Value.Kind, project.Value.Metadata, project.Value.ScopeRef, Definition = definition });
        }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender) { if (!disposed && revealMetadataErrors && !busy && definitionForm is { Busy: false }) { revealMetadataErrors = false; await definitionForm.RevealValidationAsync(); } }
    private string DownloadPath(PackProjectBuildResource build) => $"/api/pack-projects/{ProjectId:D}/builds/{build.Uid:D}/download";
    private static string ShortHash(string hash) => hash.Length <= 16 ? hash : $"{hash[..12]}…{hash[^4..]}";
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:F1} KB";
    public void Dispose() { if (disposed) return; disposed = true; ++loadVersion; cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
    private sealed record DetailBundle(ResourceSnapshot<PackProjectResource> Snapshot, IReadOnlyList<PackProjectBuildResource>? Builds, IReadOnlyList<PackProjectSourceDocument>? Sources);
    private sealed class SourceEntry(PackProjectSourceDocument document, string etag, bool initialForm) { public PackProjectSourceDocument Document { get; set; } = document; public string ETag { get; set; } = etag; public bool InitialForm { get; } = initialForm; public PackProjectSourceEditor? Component { get; set; } }
    private sealed record InstallationState(PackProjectBuildResource Build, PackInstallationPreview? Preview, Dictionary<string, string> Bindings, bool Replace, string? Baseline, Dictionary<string, string> SavedBindings, bool SavedReplace, bool IsDirty);
    private static string BindingValue(string name, ResourceNamespace @namespace, ResourceScopeRef? scopeRef = null) =>
        scopeRef is null ? $"{@namespace.Value}:{name}" : $"{scopeRef.Value.Value}|{@namespace.Value}:{name}";
    private static string BindingValue(ParameterResource resource) => BindingValue(resource.Name, resource.Namespace, resource.ScopeRef);
    private ResourceReference ParseBindingTarget(string value)
    {
        ResourceScopeRef? scopeRef = null;
        var scoped = value.Split('|', 2);
        if (scoped.Length == 2) { scopeRef = ResourceScopeRef.Parse(scoped[0]); value = scoped[1]; }
        var parts = value.Split(':', 2);
        if (parts.Length != 2) throw new InvalidOperationException(T("InvalidPackBinding"));
        return new(parts[1], scopeRef, ResourceNamespace.Parse(parts[0]));
    }
    private string BindingKindLabel(PackBindingTargetKind kind) => kind switch
    {
        PackBindingTargetKind.Secret => T("Binding.Secret"),
        PackBindingTargetKind.Parameter => T("Binding.Parameter"),
        PackBindingTargetKind.ModelProvider => T("Binding.ModelProvider"),
        PackBindingTargetKind.RuntimeProfile => T("Binding.RuntimeProfile"),
        PackBindingTargetKind.ExtensionRegistration => T("Binding.ExtensionRegistration"),
        _ => T("Binding.ModelProfile")
    };
    private string PreviewMessage(string buildVersion) => preview?.CanInstall == true
        ? T("BuildReadyForWorkspace", buildVersion)
        : preview?.AlreadyInstalled == true
            ? T("BuildAlreadyInstalled", buildVersion)
            : T("BuildConflicts", buildVersion, preview?.Namespace.ToString() ?? string.Empty);

}
