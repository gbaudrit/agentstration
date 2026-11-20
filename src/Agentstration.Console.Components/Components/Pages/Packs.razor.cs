using System.Net;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models.Contracts;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Pages;

public partial class Packs
{

    [SupplyParameterFromQuery(Name = "publisher")] public string? RequestedPublisher { get; set; }
    [SupplyParameterFromQuery(Name = "name")] public string? RequestedName { get; set; }

    private const long MaximumArchiveBytes = 8 * 1024 * 1024;
    private readonly CancellationTokenSource cancellation = new();
    private IReadOnlyList<InstalledPackResource>? packs;
    private IReadOnlyList<PackProjectResource>? projects;
    private ResourceSnapshot<InstalledPackResource>? selected;
    private string? sourceDisplayName;
    private string? sourceUrl;
    private AgentstrationApiException? error;
    private PackInstallationPreview? preview;
    private ResourceScopeRef? installScope;
    private IReadOnlyList<ModelProfileSummaryResponse> modelProfiles = [];
    private IReadOnlyList<ModelProviderResponse> modelProviders = [];
    private IReadOnlyList<RuntimeProfileSummaryResponse> runtimeProfiles = [];
    private IReadOnlyList<ExtensionRegistrationResource> extensionRegistrations = [];
    private IReadOnlyList<ParameterResource> parameters = [];
    private IReadOnlyList<SecretResponse> secrets = [];
    private readonly Dictionary<string, string> bindingSelections = new(StringComparer.Ordinal);
    private byte[]? archive;
    private string archiveName = string.Empty;
    private string? installError;
    private bool loading;
    private bool installOpen;
    private bool previewing;
    private bool installing;
    private bool replaceExisting;
    private bool confirmUninstall;
    private bool removeDashboardReferences;
    private bool forkOpen;
    private bool forking;
    private bool sourceAttaching;
    private string forkPublisher = "local";
    private string forkName = string.Empty;
    private string forkVersion = "0.1.0-dev.1";
    private string forkDisplayName = string.Empty;
    private string? forkDescription;
    private string? forkError;
    private string? forkNotice;
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;

    private int AttentionCount => packs?.Count(pack => pack.Definition.State is InstalledPackState.Failed or InstalledPackState.Degraded) ?? 0;
    private bool CanInstall => preview is not null
        && (preview.CanInstall || preview.AlreadyInstalled && replaceExisting)
        && preview.Bindings.All(binding => !binding.Required || !string.IsNullOrWhiteSpace(SelectedBinding(binding.Name)));
    private string PreviewStatus => preview is null ? T("Conflict")
        : preview.Bindings.Any(binding => binding.Required && string.IsNullOrWhiteSpace(SelectedBinding(binding.Name))) ? T("ConfigurationRequired")
        : preview.AlreadyInstalled && !replaceExisting ? T("AlreadyInstalled")
        : CanInstall ? T("Ready")
        : T("Conflict");
    private bool CanSubmitFork => !forking
        && !string.IsNullOrWhiteSpace(forkPublisher)
        && !string.IsNullOrWhiteSpace(forkName)
        && !string.IsNullOrWhiteSpace(forkVersion)
        && !string.IsNullOrWhiteSpace(forkDisplayName);

    private CancellationTokenSource? pending;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized, disposed, interactive;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.TenantId:D}|{Context.Current?.WorkspaceId:D}";
    private bool CanWrite => Context.HasPermission(AuthorizationPermissions.ResourcesWrite) && packs is not null;
    private IReadOnlyList<InstalledPackResource> Filtered => (packs ?? []).Where(pack =>
        (string.IsNullOrWhiteSpace(query.Search) || DisplayName(pack).Contains(query.Search, StringComparison.OrdinalIgnoreCase) || pack.Definition.Publisher.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || pack.Definition.PackName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("state").Value.Length == 0 || pack.Definition.State.ToString() == query.Filter("state").Value)
        && (query.Filter("purpose").Value.Length == 0 || pack.Definition.Purpose.ToString() == query.Filter("purpose").Value)
        && (query.Filter("audience").Value.Length == 0 || pack.Definition.Audience.ToString() == query.Filter("audience").Value)
        && (query.Filter("publisher").Value.Length == 0 || pack.Definition.Publisher == query.Filter("publisher").Value)
        && (query.Filter("scope").Value.Length == 0 || pack.ScopeRef?.Value == query.Filter("scope").Value)).ToArray();
    private IReadOnlyList<InstalledPackResource> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
    private CollectionPagination Pagination => new(query.Page, query.PageSize, query.Page * query.PageSize < Filtered.Count) { Total = Filtered.Count, SupportsDirectNavigation = true };
    protected override async Task OnInitializedAsync()
    {
        Context.Changed += ContextChanged;
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        contextKey = CurrentContextKey; initialized = true;
        await LoadAsync();
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }
    private Task ChangeQueryAsync(CollectionQuery next)
    {
        if (next.PageSize is not (20 or 50 or 100) || next.Page < 1) return Task.CompletedTask;
        query = next; CorrectPage(); return Task.CompletedTask;
    }
    private void CorrectPage()
    {
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling((double)Filtered.Count / query.PageSize)));
        if (page != query.Page) query = query with { Page = page };
    }
    private async Task LoadAsync()
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel(); pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { ClearCatalog(); query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            ClearCatalog(); loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed); return;
        }
        loading = true; error = null;
        loadState = new(packs is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = packs is not null };
        var primaryLoaded = false;
        try
        {
            var result = await Client.GetPacksAsync(token);
            var projectResult = await Client.GetProjectsAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            packs = result; projects = projectResult; primaryLoaded = true; CorrectPage();
            var match = !string.IsNullOrWhiteSpace(RequestedPublisher) && !string.IsNullOrWhiteSpace(RequestedName)
                ? packs.FirstOrDefault(pack => pack.Definition.Publisher == RequestedPublisher && pack.Definition.PackName == RequestedName)
                : selected is null ? null : packs.FirstOrDefault(pack => SameIdentity(pack, selected.Value));
            var selection = match is null ? null : await ReadPackAsync(match, token);
            if (!IsCurrent(requestRevision, scope)) return;
            selected = selection;
            await ResolveSelectedSourceAsync();
            if (!IsCurrent(requestRevision, scope)) return;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = exception is AgentstrationApiException api ? api.StatusCode : (exception as HttpRequestException)?.StatusCode;
            var forbidden = !primaryLoaded && status is (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
            if (forbidden) ClearCatalog();
            else if (primaryLoaded) { selected = null; sourceDisplayName = sourceUrl = null; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed) { HasContent = packs is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
        finally { if (IsCurrent(requestRevision, scope)) loading = false; }
    }
    private bool IsCurrent(long requestRevision, string scope) => !disposed && revision == requestRevision && CurrentContextKey == scope;
    private async Task RetryAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadAsync(); }
    private void ClearCatalog()
    {
        packs = null; projects = null; selected = null; sourceDisplayName = sourceUrl = null; error = null;
        installOpen = forkOpen = confirmUninstall = false;
        modelProfiles = []; modelProviders = []; runtimeProfiles = []; extensionRegistrations = []; parameters = []; secrets = [];
        archive = null; preview = null; bindingSelections.Clear();
    }
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () => { if (disposed) return; ClearCatalog(); var loadingTask = LoadAsync(); StateHasChanged(); await loadingTask; StateHasChanged(); });
    }
    private Task<ResourceSnapshot<InstalledPackResource>> ReadPackAsync(InstalledPackResource pack, CancellationToken token) => pack.ScopeRef is { } owner
        ? Client.GetPackAtScopeAsync(owner, pack.Definition.Publisher, pack.Definition.PackName, token)
        : Client.GetPackAsync(pack.Definition.Publisher, pack.Definition.PackName, token);
    private Task OpenPackAsync(InstalledPackResource pack)
    {
        if (loading || disposed) return Task.CompletedTask;
        // Existing mutation commands resolve in the selected context. Switch to the owner
        // before exposing its inspection panel and commands. Inspection itself is explicitly scoped.
        if (pack.ScopeRef is { } owner && ResourceOwnershipContext.TryGetCurrent(Services) is { } current && current != owner)
        {
            Navigation.NavigateTo($"/packs?publisher={Uri.EscapeDataString(pack.Definition.Publisher)}&name={Uri.EscapeDataString(pack.Definition.PackName)}&context={Uri.EscapeDataString(owner.Value)}", forceLoad: true);
            return Task.CompletedTask;
        }
        return SelectAsync(pack);
    }

    private void OpenInstall()
    {
        preview = null;
        installScope = null;
        archive = null;
        archiveName = string.Empty;
        installError = null;
        replaceExisting = false;
        bindingSelections.Clear();
        installOpen = true;
    }

    private void CloseInstall()
    {
        if (installing || previewing) return;
        installOpen = false;
    }

    private async Task ReadArchiveAsync(InputFileChangeEventArgs args)
    {
        previewing = true;
        preview = null;
        installScope = null;
        archive = null;
        installError = null;
        replaceExisting = false;
        archiveName = args.File.Name;
        try
        {
            await using var input = args.File.OpenReadStream(MaximumArchiveBytes, cancellation.Token);
            await using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellation.Token);
            archive = output.ToArray();
            preview = await Client.PreviewAsync(archive, archiveName, cancellation.Token);
            bindingSelections.Clear();
            foreach (var binding in preview.Bindings)
            {
                if (binding.TargetAvailable && binding.Target is not null)
                    bindingSelections[binding.Name] = BindingValue(binding.Target.Name, binding.Target.Namespace ?? ResourceNamespace.Default, binding.Target.ScopeRef);
            }
            if (preview.Bindings.Count > 0)
            {
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.ModelProfile))
                    modelProfiles = await Services.GetRequiredService<IModelProfilesClient>().GetModelProfilesAsync(null, null, null, cancellation.Token);
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.ModelProvider))
                    modelProviders = await Services.GetRequiredService<IModelProvidersClient>().GetModelProvidersAsync(cancellation.Token);
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.RuntimeProfile))
                    runtimeProfiles = await Services.GetRequiredService<IRuntimeProfilesClient>().GetRuntimeProfilesAsync(cancellation.Token);
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.ExtensionRegistration))
                    extensionRegistrations = await Services.GetRequiredService<IExtensionsClient>().GetRegistrationsAsync(cancellation.Token);
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.Parameter))
                    parameters = await Services.GetRequiredService<IParametersClient>().GetParametersAsync(cancellation.Token);
                if (preview.Bindings.Any(binding => binding.TargetKind == PackBindingTargetKind.Secret))
                    secrets = await Services.GetRequiredService<ISecretsClient>().GetSecretsAsync(cancellation.Token);
            }
        }
        catch (AgentstrationApiException exception) { installError = exception.Message; }
        catch (IOException exception) { installError = exception.Message; }
        finally { previewing = false; }
    }

    private async Task InstallAsync()
    {
        if (archive is null || !CanInstall) return;
        installing = true;
        installError = null;
        try
        {
            var bindings = preview!.Bindings
                .Where(binding => !string.IsNullOrWhiteSpace(SelectedBinding(binding.Name)))
                .Select(binding => new PackBindingSelection(binding.Name, ParseBindingTarget(SelectedBinding(binding.Name)!)))
                .ToArray();
            selected = await Client.InstallAsync(archive, archiveName, replaceExisting, removeDashboardReferences, bindings, cancellation.Token);
            installOpen = false;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { installError = exception.Message; }
        finally { installing = false; }
    }

    private async Task SelectAsync(InstalledPackResource pack)
    {
        var requestRevision = ++revision;
        var scope = CurrentContextKey;
        error = null;
        try
        {
            var selection = await ReadPackAsync(pack, cancellation.Token);
            if (!IsCurrent(requestRevision, scope)) return;
            selected = selection;
            await ResolveSelectedSourceAsync();
        }
        catch (AgentstrationApiException exception) { if (IsCurrent(requestRevision, scope)) { error = exception; if (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { selected = null; sourceDisplayName = sourceUrl = null; } } }
    }

    private async Task ResolveSelectedSourceAsync()
    {
        var selection = selected;
        var requestRevision = revision;
        var scope = CurrentContextKey;
        sourceDisplayName = null;
        sourceUrl = null;
        if (selected?.Value.Definition.SourceProvenance is not { } provenance) return;
        var sourceService = Services.GetService<ISourceConsoleApiClient>();
        if (sourceService is null) return;

        try
        {
            var source = (await sourceService.ListAsync(cancellation.Token))
                .SingleOrDefault(item => item.Source.Source.Uid == provenance.SourceUid);
            if (source is null || !IsCurrent(requestRevision, scope) || selected != selection) return;

            sourceDisplayName = source.Source.Configuration.Definition.DisplayName;
            var resource = source.Source.Source;
            var scopeRef = resource.ScopeRef
                ?? throw new InvalidOperationException($"Source '{resource.Address}' has no ownership scope.");
            sourceUrl = $"/settings/sources/{Uri.EscapeDataString(resource.Definition.Publisher)}/{Uri.EscapeDataString(resource.Name)}?scopeRef={Uri.EscapeDataString(scopeRef.Value.ToString())}";
        }
        catch (Exception exception) when (exception is AuthorizationDeniedException or AgentstrationApiException or HttpRequestException)
        {
            // Optional Source label lookup cannot hide an otherwise authorized Pack. Its pinned provenance remains visible.
        }
    }

    private void OpenFork()
    {
        if (selected is null) return;
        forkPublisher = "local";
        forkName = $"{selected.Value.Definition.PackName}-fork";
        forkVersion = "0.1.0-dev.1";
        forkDisplayName = T("ForkDisplayName", DisplayName(selected.Value));
        forkDescription = selected.Value.Definition.Description;
        forkError = null;
        forkNotice = null;
        forkOpen = true;
    }

    private void CloseFork() { if (!forking) forkOpen = false; }

    private async Task ForkAsync()
    {
        if (selected is null) return;
        forking = true;
        forkError = null;
        try
        {
            var project = await Client.ForkAsync(selected.Value.Definition.Publisher, selected.Value.Definition.PackName,
                new(forkPublisher.Trim(), forkName.Trim(), forkVersion.Trim(), forkDisplayName.Trim(), forkDescription?.Trim()), cancellation.Token);
            Navigation.NavigateTo($"/pack-projects/{project.Value.Uid:D}");
        }
        catch (AgentstrationApiException exception) { forkError = exception.Message; }
        finally { forking = false; }
    }

    private async Task AttachSourceAsync(InputFileChangeEventArgs args)
    {
        if (selected is null) return;
        sourceAttaching = true;
        forkError = null;
        forkNotice = null;
        try
        {
            await using var input = args.File.OpenReadStream(MaximumArchiveBytes, cancellation.Token);
            await using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellation.Token);
            selected = await Client.AttachSourceAsync(selected.Value.Definition.Publisher, selected.Value.Definition.PackName,
                output.ToArray(), args.File.Name, selected.ETag, cancellation.Token);
            forkNotice = T("SourceArchiveAttached");
        }
        catch (AgentstrationApiException exception) { forkError = exception.Message; }
        catch (IOException exception) { forkError = exception.Message; }
        finally { sourceAttaching = false; }
    }

    private void OpenProject(Guid projectId) => Navigation.NavigateTo($"/pack-projects/{projectId:D}");

    private async Task UninstallAsync()
    {
        if (selected is null) return;
        confirmUninstall = false;
        try
        {
            await Client.UninstallAsync(selected.Value.Definition.Publisher, selected.Value.Definition.PackName, selected.ETag, removeDashboardReferences, cancellation.Token);
            selected = null;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
    }

    private bool IsSelected(InstalledPackResource pack) => selected is not null && SameIdentity(pack, selected.Value);
    private static bool SameIdentity(InstalledPackResource left, InstalledPackResource right) => left.ScopeRef == right.ScopeRef && left.Namespace == right.Namespace && left.Definition.Publisher == right.Definition.Publisher && left.Definition.PackName == right.Definition.PackName;
    private static string DisplayName(InstalledPackResource pack) => pack.Definition.DisplayName ?? pack.Definition.PackName;
    private string? SelectedBinding(string name) => bindingSelections.GetValueOrDefault(name);
    private void BindingChanged(string name, ChangeEventArgs args) => bindingSelections[name] = args.Value?.ToString() ?? string.Empty;
    private static string BindingValue(string name, ResourceNamespace @namespace, ResourceScopeRef? scopeRef = null) =>
        scopeRef is null ? $"{@namespace.Value}:{name}" : $"{scopeRef.Value.Value}|{@namespace.Value}:{name}";
    private string? DefinitionFormUrl(ManagedPackResource resource) => resource.Kind switch
    {
        AgentResourceKinds.Agent => ConsoleResourceUrls.Agent(resource.Namespace, resource.Name, selected?.Value.ScopeRef, ResourceOwnershipContext.TryGetCurrent(Services)) + "#definition/configuration",
        FlowResourceKinds.Flow => $"/namespaces/{Uri.EscapeDataString(resource.Namespace.Value)}/flows/{Uri.EscapeDataString(resource.Name)}?view=definition",
        EntryResourceKinds.Entry => $"/namespaces/{Uri.EscapeDataString(resource.Namespace.Value)}/entries/{Uri.EscapeDataString(resource.Name)}?view=definition",
        _ => null
    };
    private static string BindingTarget(ResourceReference target) => $"{(target.Namespace ?? ResourceNamespace.Default).Value}/{target.Name}";
    private ResourceReference ParseBindingTarget(string value)
    {
        var scoped = value.Split('|', 2);
        var parts = scoped[^1].Split(':', 2);
        if (parts.Length != 2) throw new InvalidOperationException(T("InvalidPackBinding"));
        ResourceScopeRef? scopeRef = scoped.Length == 2 && !string.IsNullOrWhiteSpace(scoped[0]) ? ResourceScopeRef.Parse(scoped[0]) : null;
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
    private string PurposeLabel(PackPurpose purpose) => T($"Purpose.{purpose}").ToUpperInvariant();
    private string AudienceLabel(PackAudience audience) => T($"Audience.{audience}");
    private string StateLabel(InstalledPackState state) => T($"InstalledState.{state}");
    private static UiStatus StateTone(InstalledPackState state) => state switch { InstalledPackState.Installed => UiStatus.Success, InstalledPackState.Failed => UiStatus.Danger, InstalledPackState.Degraded => UiStatus.Warning, _ => UiStatus.Info };
    private static UiStatus ChangeTone(PackResourceChange change) => change switch { PackResourceChange.Add => UiStatus.Success, PackResourceChange.Conflict => UiStatus.Danger, PackResourceChange.Remove => UiStatus.Warning, _ => UiStatus.Info };

    public void Dispose()
    {
        disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose();
        cancellation.Cancel();
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
