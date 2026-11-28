using System.Net;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Models.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Pages;

public partial class ExtensionDetails
{
    [Parameter] public string? RegistrationName { get; set; }
    [Parameter] public Guid? EnrollmentInstanceId { get; set; }
    [SupplyParameterFromQuery(Name = "namespace")] public string? ResourceNamespaceValue { get; set; }
    [SupplyParameterFromQuery(Name = "scopeRef")] public string? ScopeRefValue { get; set; }
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly CancellationTokenSource cancellation = new();
    private readonly ResourceSectionState sectionState = new();
    private ConsoleContextState? contextState;
    private ExtensionInventoryItemResponse? item;
    private AepEnrollmentRequestResource? enrollment;
    private ResourceSnapshot<ExtensionRegistrationResource>? registration;
    private ExtensionBundle? newerBundle;
    private string? commandError, loadedKey, baseline, observedPermissions;
    private bool installationDraft;
    private bool refreshing, saving, migrating, invalidInput, confirmDelete, confirmUnenroll, disposed;
    private long loadVersion;
    private IReadOnlyList<ToolResource> extensionTools = [];
    private ToolProviderResource? extensionToolProvider;
    private ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>? migrationPreview;
    private ExtensionRegistrationEditorModel form = new();
    private EditContext? editContext;
    private ResourceDefinitionForm? definitionForm;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private ResourceLoadState? enrollmentLoadState;
    private Resource? SavedResource => RegistrationName is not null ? registration?.Value : enrollment;
    private Resource? ResourceFor(ExtensionBundle bundle) => RegistrationName is not null ? bundle.Registration?.Value : bundle.Enrollment;
    private bool Busy => refreshing || saving || migrating;
    private bool HasDefinitionDraft => SavedResource is not null && baseline is not null && (Fingerprint != baseline || invalidInput);
    private string Fingerprint => JsonSerializer.Serialize(form);
    private bool CanAdminister => contextState?.HasPermission(AuthorizationPermissions.ResourcesWrite) == true;
    private bool CanEditRegistration => RegistrationName is not null && CanAdminister && registration is { Value.ScopeRef: not null }
        && registration.Value.Definition.Source == ExtensionRegistrationSource.Manual && !registration.Value.Governance.IsManaged
        && ResourceOwnershipContext.CanManage(registration.Value.ScopeRef, Services);
    private bool CanDeleteRegistration => CanEditRegistration && contextState?.HasPermission(AuthorizationPermissions.ResourcesDelete) == true;
    private string ContextKey => $"{ResourceOwnershipContext.TryGetCurrent(Services)}|{contextState?.Current?.UserId}|{contextState?.Current?.SelectedScopeRef}";
    private string? ToolPreviewContextKey => contextState?.Current is { } current ? $"{RegistrationName}|{ResourceNamespaceValue}|{ScopeRefValue}|{current.UserId}|{current.SelectedScopeRef}|{current.WorkspaceId}" : null;
    private string RouteKey => $"{RegistrationName}|{EnrollmentInstanceId}|{ResourceNamespaceValue}|{ScopeRefValue}|{ContextKey}";
    private string SavedDisplayName => RegistrationName is not null ? registration?.Value.Definition.DisplayName ?? RegistrationName : enrollment?.Definition.ExtensionName ?? item?.DisplayName ?? T("Extension");
    private string BackUrl => ResourceOwnershipContext.PreserveNavigation("/extensions", Services);
    private string EnrollmentManagementUrl => ResourceOwnershipContext.PreserveNavigation(item?.EnrollmentInstanceId is { } instanceId ? $"/extensions?tab=enrollments&enrollment={instanceId:D}" : "/extensions?tab=enrollments", Services);
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private string StatusLabel(string status) => T($"Status.{status}");
    private string EnrollmentStatusLabel(AepEnrollmentState status) => T($"EnrollmentStatus.{status}");
    private string SourceLabel(string source) => T($"Source.{JsonNamingPolicy.CamelCase.ConvertName(source)}");
    private EditContext FormContext => editContext ??= new(form);
    private IReadOnlyDictionary<string, ResourceSectionPresentation> SectionPresentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { ReadOnly = !CanEditRegistration, HasRequiredFields = true, Fields = [nameof(form.Name), nameof(form.DisplayName), nameof(form.Namespace), nameof(form.Description), nameof(form.Tags)] },
        ["definition/configuration"] = new() { ReadOnly = !CanEditRegistration, HasRequiredFields = true, Fields = RegistrationName is null ? [nameof(form.Endpoint)] : [nameof(form.Endpoint), nameof(form.ExpectedExtensionId), nameof(form.Enabled)] },
        ["yaml"] = new() { ReadOnly = true, ReadOnlyMessage = T("YamlReadOnlyProjection") }
    };
    private IReadOnlyDictionary<string, string> FieldLabels => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(form.Name)] = IdentityLocalizer["TechnicalName"],
        [nameof(form.DisplayName)] = IdentityLocalizer["DisplayName"],
        [nameof(form.Namespace)] = Shared["Namespace"],
        [nameof(form.Description)] = Shared["Description"],
        [nameof(form.Tags)] = Shared["Labels"],
        [nameof(form.Endpoint)] = T("AepEndpoint"),
        [nameof(form.ExpectedExtensionId)] = T("ExpectedExtensionId")
    };
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections =>
    [
        new("installation", T("Installation.Title"), "adjustments"), new("enrollment", T("DetailTab.Enrollment"), "key"), new("contributions", T("DetailTab.Contributions"), "puzzle"),
        new("resources", T("DetailTab.Resources"), "stack"), new("options", T("DetailTab.Options"), "adjustments"), new("usages", T("DetailTab.Usages"), "list")
    ];
    private IReadOnlyList<ResourceNavigationItem> OperationalSections => [new("overview", Shared["Overview"], "layout-dashboard"), .. AdditionalSections];
    private sealed record ConfigurationEntry(string Label, string Value, string? Href = null);
    // An enrollment has private code verifier fields. Only its public inspection projection is exported.
    private string SavedYaml => RegistrationName is not null && registration is { } saved ? ResourceManifestSerializer.ToYaml(saved.Value)
        : enrollment is { } request ? ResourceManifestSerializer.ToYaml(new
        {
            request.ApiVersion,
            request.Kind,
            request.Metadata,
            request.ScopeRef,
            request.Uid,
            request.Generation,
            request.Governance,
            Definition = new
            {
                request.Definition.InstanceId,
                request.Definition.TargetScopeRef,
                request.Definition.TargetTenantId,
                request.Definition.ExtensionId,
                request.Definition.ExtensionName,
                request.Definition.ExtensionVersion,
                request.Definition.Endpoint,
                request.Definition.PairingUri,
                request.Definition.EnrollmentMode,
                request.Definition.State,
                request.Definition.AnnouncedAt,
                request.Definition.CodeIssuedAt,
                request.Definition.CodeExpiresAt,
                request.Definition.AttemptCount,
                request.Definition.RegistrationName,
                request.Definition.Outcome
            }
        }) : string.Empty;
    protected override void OnInitialized()
    {
        contextState = Services.GetService<ConsoleContextState>();
        if (contextState is not null) contextState.Changed += ContextChanged;
    }
    private void ContextChanged() => _ = InvokeAsync(async () =>
    {
        if (disposed) return;
        try
        {
            if (loadedKey != RouteKey) { await LoadAsync(false); return; }
            var permissions = string.Join(",", (contextState?.Current?.Permissions ?? new HashSet<string>()).Order(StringComparer.Ordinal));
            if (observedPermissions == permissions) return;
            observedPermissions = permissions;
            if (contextState?.Current is null || !contextState.HasPermission(AuthorizationPermissions.ResourcesRead))
            { ++loadVersion; ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); StateHasChanged(); return; }
            saving = migrating = false;
            if (!CanAdminister) { enrollment = null; migrationPreview = null; }
            await LoadAsync(true);
        }
        finally { if (!disposed) StateHasChanged(); }
    });
    protected override Task OnParametersSetAsync() => loadedKey == RouteKey ? Task.CompletedTask : LoadAsync(false);
    private bool Current(long version, string key) => !disposed && version == loadVersion && key == RouteKey;
    private Task RefreshAsync() => Busy ? Task.CompletedTask : LoadAsync(true);
    private async Task LoadAsync(bool preserveDraft)
    {
        var key = RouteKey; var revision = ++loadVersion; loadedKey = key;
        observedPermissions = string.Join(",", (contextState?.Current?.Permissions ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        refreshing = preserveDraft && item is not null;
        if (!preserveDraft) { ClearPrivate(); sectionState.Reset(key); }
        loadState = new(refreshing ? ResourceLoadStatus.Refreshing : ResourceLoadStatus.Loading) { HasContent = item is not null };
        commandError = null;
        try
        {
            if (contextState is not null)
            {
                await contextState.EnsureLoadedAsync(cancellation.Token);
                if (!Current(revision, key)) return;
                if (contextState.Current is null) throw new InvalidOperationException(contextState.Error);
            }
            var bundle = await ReadBundleAsync(revision, key);
            if (bundle is null || !Current(revision, key)) return;
            if (SavedResource is { } previous && ResourceFor(bundle) is { } actual && !SameIdentity(previous, actual))
                throw new IdentityMismatchException(T("IdentityMismatch"));
            var changed = registration?.ETag != bundle.Registration?.ETag || ResourceFingerprint(SavedResource) != ResourceFingerprint(ResourceFor(bundle));
            if (preserveDraft && (HasDefinitionDraft || migrationPreview is not null) && changed) newerBundle = bundle;
            else if (!preserveDraft || changed || SavedResource is null) Adopt(bundle);
            else ApplyObservation(bundle);
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!Current(revision, key)) return;
            var lost = exception is IdentityMismatchException || exception is AgentstrationApiException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized };
            if (lost) ClearPrivate();
            loadState = new(exception is AgentstrationApiException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } ? ResourceLoadStatus.Forbidden
                : exception is IdentityMismatchException or AgentstrationApiException { StatusCode: HttpStatusCode.NotFound } ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Failed)
            { HasContent = item is not null, Message = exception.Message, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
        finally { if (Current(revision, key)) refreshing = false; }
    }
    private async Task<ExtensionBundle?> ReadBundleAsync(long revision, string key)
    {
        var inventory = await Client.GetExtensionInventoryAsync(cancellation.Token);
        if (!Current(revision, key)) return null;
        var owner = string.IsNullOrWhiteSpace(ScopeRefValue) ? (ResourceScopeRef?)null : ResourceScopeRef.Parse(ScopeRefValue);
        var selected = RegistrationName is not null
            ? inventory.SingleOrDefault(value => value.RegistrationName == RegistrationName && value.RegistrationNamespace == (ResourceNamespaceValue ?? ResourceNamespace.DefaultValue) && (owner is null || value.RegistrationScopeRef == owner))
            : inventory.SingleOrDefault(value => value.EnrollmentInstanceId == EnrollmentInstanceId);
        if (selected is null) throw new AgentstrationApiException(T("ExtensionNotFound"), string.Empty, HttpStatusCode.NotFound);
        ResourceSnapshot<ExtensionRegistrationResource>? saved = null;
        if (selected.RegistrationName is { } name)
        {
            saved = selected.RegistrationScopeRef is { } scope
                ? await Client.GetRegistrationAsync(scope, ResourceNamespace.Parse(selected.RegistrationNamespace), name, cancellation.Token)
                : await Client.GetRegistrationAsync(ResourceNamespace.Parse(selected.RegistrationNamespace), name, cancellation.Token);
            if (!Current(revision, key)) return null;
            if (saved.Value.Name != name || saved.Value.Namespace.Value != selected.RegistrationNamespace || selected.RegistrationScopeRef is { } expected && saved.Value.ScopeRef != expected)
                throw new IdentityMismatchException(T("IdentityMismatch"));
            if (RegistrationName is not null && registration is { } previous && !SameIdentity(previous.Value, saved.Value))
                throw new IdentityMismatchException(T("IdentityMismatch"));
        }
        AepEnrollmentRequestResource? request = null;
        ResourceLoadState? enrollmentState = null;
        if (CanAdminister && selected.EnrollmentInstanceId is { } instanceId)
        {
            try
            {
                var requests = await Client.GetEnrollmentsAsync(cancellation.Token);
                if (!Current(revision, key)) return null;
                request = requests.SingleOrDefault(value => value.Definition.InstanceId == instanceId);
            }
            catch (Exception exception) when (RegistrationName is not null && exception is (AgentstrationApiException or HttpRequestException))
            {
                var status = (exception as AgentstrationApiException)?.StatusCode ?? (exception as HttpRequestException)?.StatusCode;
                enrollmentState = new(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? ResourceLoadStatus.Forbidden
                    : status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Failed)
                { ErrorId = (exception as AgentstrationApiException)?.ErrorId };
            }
            if (request is not null && (request.Definition.InstanceId != selected.EnrollmentInstanceId
                || request.Definition.RegistrationName is { } registered && selected.RegistrationName is { } expectedName && registered != expectedName
                || request.Definition.TargetScopeRef is { } target && selected.RegistrationScopeRef is { } registrationOwner && target != registrationOwner))
                throw new IdentityMismatchException(T("IdentityMismatch"));
            if (RegistrationName is null && enrollment is { } previous && request is { } actual && !SameIdentity(previous, actual))
                throw new IdentityMismatchException(T("IdentityMismatch"));
        }
        IReadOnlyList<ToolResource> tools = []; ToolProviderResource? provider = null;
        if (selected.RegistrationName is not null && Services.GetService<IToolsClient>() is { } toolsClient)
        {
            try
            {
                var candidates = selected.RegistrationScopeRef is { } scope ? await toolsClient.GetToolsAsync(scope, cancellationToken: cancellation.Token) : await toolsClient.GetToolsAsync(cancellationToken: cancellation.Token);
                if (!Current(revision, key)) return null;
                tools = candidates.Where(value => ToolBelongsToExtension(value, selected)).ToArray();
                var providers = await toolsClient.GetProvidersAsync(cancellation.Token);
                if (!Current(revision, key)) return null;
                provider = providers.SingleOrDefault(value => value.Name == selected.RegistrationName && value.Namespace.Value == selected.RegistrationNamespace && value.ScopeRef == selected.RegistrationScopeRef
                    && value.Definition.Aep?.Extension.Name == selected.RegistrationName && (value.Definition.Aep.Extension.Namespace ?? value.Namespace).Value == selected.RegistrationNamespace
                    && (value.Definition.Aep.Extension.ScopeRef ?? value.ScopeRef) == selected.RegistrationScopeRef);
            }
            catch (AgentstrationApiException) { /* This optional inventory does not grant registration or execution access. */ }
        }
        return new(selected, saved, request, tools, provider, enrollmentState);
    }
    private sealed record ExtensionBundle(ExtensionInventoryItemResponse Item, ResourceSnapshot<ExtensionRegistrationResource>? Registration, AepEnrollmentRequestResource? Enrollment, IReadOnlyList<ToolResource> Tools, ToolProviderResource? Provider, ResourceLoadState? EnrollmentState);
    private sealed class IdentityMismatchException(string message) : Exception(message);
    private static bool SameIdentity(Resource expected, Resource actual) => expected.Kind == actual.Kind && expected.Name == actual.Name && expected.Namespace == actual.Namespace && expected.ScopeRef == actual.ScopeRef
        && (expected.Uid == Guid.Empty || expected.Uid == actual.Uid);
    private static string ResourceFingerprint(Resource? value) => value switch
    { ExtensionRegistrationResource registration => JsonSerializer.Serialize(registration), AepEnrollmentRequestResource enrollment => JsonSerializer.Serialize(enrollment), _ => string.Empty };
    private void ApplyObservation(ExtensionBundle bundle) { item = bundle.Item; enrollment = bundle.Enrollment; enrollmentLoadState = bundle.EnrollmentState; extensionTools = bundle.Tools; extensionToolProvider = bundle.Provider; }
    private void Adopt(ExtensionBundle bundle)
    {
        registration = bundle.Registration; ApplyObservation(bundle);
        form = ExtensionRegistrationEditorModel.From(ResourceFor(bundle), bundle.Item); editContext = null; invalidInput = false;
        baseline = Fingerprint; newerBundle = null;
    }
    private void ClearPrivate()
    {
        installationDraft = false;
        item = null; registration = null; enrollment = null; enrollmentLoadState = null; newerBundle = null; migrationPreview = null; extensionTools = []; extensionToolProvider = null;
        form = new(); editContext = null; baseline = null; invalidInput = false; confirmDelete = confirmUnenroll = false; saving = migrating = false;
    }
    private IReadOnlyList<ConfigurationEntry> EnrollmentConfiguration => enrollment is not { } request ? [] :
    [
        new(T("ExtensionId"), request.Definition.ExtensionId), new(T("ObservedVersion"), request.Definition.ExtensionVersion),
        new(T("AepEndpoint"), request.Definition.Endpoint.AbsoluteUri), new(T("EnrollmentState"), EnrollmentStatusLabel(request.Definition.State)),
        new(T("AnnouncedAt"), FormatDate(request.Definition.AnnouncedAt)), new(T("CodeIssuedAt"), FormatDate(request.Definition.CodeIssuedAt)),
        new(T("CodeExpiresAt"), FormatDate(request.Definition.CodeExpiresAt)), new(T("AttemptCount"), request.Definition.AttemptCount.ToString(System.Globalization.CultureInfo.CurrentCulture)),
        new(T("Registration"), request.Definition.RegistrationName ?? "—"), new(T("Outcome"), request.Definition.Outcome ?? "—")
    ];
    private void EditIdentity() => ShowSection("definition/identity");
    private void ShowSection(string path) => Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path);
    private void OperationalSectionSelected(string? path) { if (path is not null) sectionState.Select(path); }
    private void Discard()
    {
        if (Busy || SavedResource is null) return;
        if (newerBundle is { } newer) Adopt(newer);
        else if (item is { } selected) Adopt(new(selected, registration, enrollment, extensionTools, extensionToolProvider, enrollmentLoadState));
        commandError = null;
    }
    private async Task SaveAsync()
    {
        if (!CanEditRegistration || Busy || registration is null || !FormContext.Validate() || invalidInput || !Uri.TryCreate(form.Endpoint, UriKind.Absolute, out var endpoint)) return;
        var current = registration; var revision = loadVersion; var key = RouteKey; saving = true; commandError = null;
        try
        {
            var saved = await Client.UpdateRegistrationAsync(current.Value.Namespace, current.Value.Name,
                new(current.Value.Definition with { DisplayName = form.DisplayName, Endpoint = endpoint, ExpectedExtensionId = form.ExpectedExtensionId }), current.ETag, cancellation.Token);
            if (!Current(revision, key)) return;
            if (!SameIdentity(current.Value, saved.Value)) throw new IdentityMismatchException(T("IdentityMismatch"));
            registration = saved; item = item! with { DisplayName = saved.Value.Definition.DisplayName, Endpoint = saved.Value.Definition.Endpoint, RegistrationEnabled = saved.Value.Definition.Enabled };
            form = ExtensionRegistrationEditorModel.From(saved.Value, item); editContext = null; baseline = Fingerprint; invalidInput = false; newerBundle = null;
            saving = false; await LoadAsync(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) saving = false; }
    }
    private async Task SetEnabledAsync(bool enabled)
    {
        if (!CanEditRegistration || Busy || registration is null || registration.Value.Definition.Enabled == enabled) return;
        var current = registration; var revision = loadVersion; var key = RouteKey; saving = true; commandError = null;
        try
        {
            var saved = await Client.UpdateRegistrationAsync(current.Value.Namespace, current.Value.Name, new(current.Value.Definition with { Enabled = enabled }), current.ETag, cancellation.Token);
            if (!Current(revision, key)) return;
            if (!SameIdentity(current.Value, saved.Value)) throw new IdentityMismatchException(T("IdentityMismatch"));
            registration = saved; item = item! with { RegistrationEnabled = saved.Value.Definition.Enabled };
            form.Enabled = saved.Value.Definition.Enabled;
            baseline = JsonSerializer.Serialize(ExtensionRegistrationEditorModel.From(saved.Value, item)); newerBundle = null;
            saving = false; await LoadAsync(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) saving = false; }
    }
    private string? returnAfterDelete;
    protected override void OnAfterRender(bool firstRender) { if (returnAfterDelete is { } destination) { returnAfterDelete = null; Navigation.NavigateTo(destination); } }
    private async Task DeleteAsync()
    {
        if (!CanDeleteRegistration || Busy || registration is null) return;
        var current = registration; var revision = loadVersion; var key = RouteKey; saving = true; confirmDelete = false;
        try { await Client.DeleteRegistrationAsync(current.Value.Namespace, current.Value.Name, current.ETag, cancellation.Token); if (!Current(revision, key)) return; ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); returnAfterDelete = BackUrl; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) saving = false; }
    }
    private async Task UnenrollAsync()
    {
        if (!CanAdminister || Busy || enrollment is null || !CanUnenroll(enrollment.Definition.State)) return;
        var current = enrollment; var revision = loadVersion; var key = RouteKey; saving = true; confirmUnenroll = false;
        try
        {
            await Client.UnenrollExtensionAsync(current.Definition.InstanceId, cancellation.Token);
            if (!Current(revision, key)) return;
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("ExtensionUnenrolled"), T("ExtensionUnenrolled.Description"), DateTimeOffset.Now, UiStatus.Success));
            saving = false; await LoadAsync(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) saving = false; }
    }
    private async Task PreviewMigrationAsync(ExtensionOptionUsageResponse usage, string targetVersion)
    {
        if (!CanAdminister || Busy || item?.Extension is not { } extension || !extension.Usages.Contains(usage) || MigrationTarget(extension, usage) != targetVersion) return;
        var revision = loadVersion; var key = RouteKey; migrating = true; commandError = null;
        try
        {
            var preview = await ProfilesClient.PreviewOptionMigrationAsync(ResourceNamespace.Parse(usage.ProfileNamespace), usage.ProfileName, targetVersion, cancellation.Token);
            if (!Current(revision, key)) return;
            if (preview.Value.ProfileName != usage.ProfileName || preview.Value.ProfileNamespace != usage.ProfileNamespace || preview.Value.Target.Version != targetVersion)
                throw new IdentityMismatchException(T("IdentityMismatch"));
            migrationPreview = preview;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) migrating = false; }
    }
    private void CancelMigration() { if (!Busy) migrationPreview = null; }
    private async Task ApplyMigrationAsync()
    {
        if (!CanAdminister || Busy || migrationPreview is null) return;
        var preview = migrationPreview; var revision = loadVersion; var key = RouteKey; migrating = true; commandError = null;
        try
        {
            var saved = await ProfilesClient.ApplyOptionMigrationAsync(ResourceNamespace.Parse(preview.Value.ProfileNamespace), preview.Value.ProfileName, preview.Value.Target.Version, preview.ETag, cancellation.Token);
            if (!Current(revision, key)) return;
            if (saved.Value.Name != preview.Value.ProfileName || saved.Value.Namespace.Value != preview.Value.ProfileNamespace) throw new IdentityMismatchException(T("IdentityMismatch"));
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("ExtensionOptionsMigrated"), $"{preview.Value.ProfileName}: {preview.Value.Source.Version} → {preview.Value.Target.Version}", DateTimeOffset.Now, UiStatus.Success));
            migrationPreview = null; migrating = false; await LoadAsync(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(revision, key)) commandError = exception.Message; }
        finally { if (Current(revision, key)) migrating = false; }
    }
    private static bool ToolBelongsToExtension(ToolResource tool, ExtensionInventoryItemResponse selected) => selected.RegistrationName is { } name
        && tool.Definition.Provider?.Name == name && (tool.Definition.Provider.Namespace ?? tool.Namespace).Value == selected.RegistrationNamespace
        && (tool.Definition.Provider.ScopeRef ?? tool.ScopeRef) == selected.RegistrationScopeRef && (selected.RegistrationScopeRef is null || tool.ScopeRef == selected.RegistrationScopeRef);
    private static string? MigrationTarget(ExtensionResponse extension, ExtensionOptionUsageResponse usage)
    {
        var optionSet = extension.OptionSets.SingleOrDefault(value => string.Equals(value.Id, usage.OptionSet, StringComparison.Ordinal));
        if (optionSet is null || string.Equals(optionSet.PreferredVersion, usage.Version, StringComparison.Ordinal)) return null;
        var pending = new Queue<string>(); var visited = new HashSet<string>(StringComparer.Ordinal) { usage.Version }; pending.Enqueue(usage.Version);
        while (pending.TryDequeue(out var current)) foreach (var migration in optionSet.Migrations.Where(value => string.Equals(value.FromVersion, current, StringComparison.Ordinal))) { if (string.Equals(migration.ToVersion, optionSet.PreferredVersion, StringComparison.Ordinal)) return optionSet.PreferredVersion; if (visited.Add(migration.ToVersion)) pending.Enqueue(migration.ToVersion); }
        return null;
    }
    private static bool CanUnenroll(AepEnrollmentState state) => state is AepEnrollmentState.Available or AepEnrollmentState.Disabled or AepEnrollmentState.VerificationFailed or AepEnrollmentState.Unenrolling;
    private static UiStatus EnrollmentStatus(AepEnrollmentState state) => state switch { AepEnrollmentState.Available => UiStatus.Success, AepEnrollmentState.CodeIssued or AepEnrollmentState.CredentialIssued or AepEnrollmentState.Verifying or AepEnrollmentState.Unenrolling => UiStatus.Warning, AepEnrollmentState.Rejected or AepEnrollmentState.Cancelled or AepEnrollmentState.Expired or AepEnrollmentState.AttemptsExceeded or AepEnrollmentState.VerificationFailed or AepEnrollmentState.Revoked => UiStatus.Danger, _ => UiStatus.Neutral };
    private static string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    private static string FormatDate(DateTimeOffset? value) => value is { } date ? FormatDate(date) : "—";
    private string ProfileUrl(ExtensionOptionUsageResponse usage) => ResourceOwnershipContext.PreserveNavigation($"/modelprofiles/{Uri.EscapeDataString(usage.ProfileName)}?namespace={Uri.EscapeDataString(usage.ProfileNamespace)}", Services);
    private string ProviderUrl(ExtensionProviderBindingResponse provider) => ResourceOwnershipContext.PreserveNavigation($"/modelproviders/{Uri.EscapeDataString(provider.Name)}?namespace={Uri.EscapeDataString(provider.Namespace)}", Services);
    private static string ConfigureProviderUrl(ExtensionResponse extension, ExtensionContributionResponse contribution) => $"/modelproviders/new?extension={Uri.EscapeDataString(extension.RegistrationName)}&extensionNamespace={Uri.EscapeDataString(extension.RegistrationNamespace)}&contributionId={Uri.EscapeDataString(contribution.Id)}&displayName={Uri.EscapeDataString(extension.Extension?.Name ?? contribution.Id)}";
    private bool ToolBelongsToExtension(ToolResource tool) => item?.RegistrationName is { } registrationName
        && string.Equals(tool.Definition.Provider?.Name, registrationName, StringComparison.Ordinal)
        && (tool.Definition.Provider?.Namespace ?? tool.Namespace).Value == item.RegistrationNamespace
        && (tool.Definition.Provider?.ScopeRef ?? tool.ScopeRef) == item.RegistrationScopeRef
        && (item.RegistrationScopeRef is null || tool.ScopeRef == item.RegistrationScopeRef);
    private IReadOnlyList<ToolResource> MaterializedTools(ExtensionContributionResponse contribution) =>
        string.Equals(contribution.Kind, "tool", StringComparison.Ordinal)
            ? extensionTools.Where(tool => string.Equals(tool.Definition.ExternalId, contribution.Id, StringComparison.Ordinal)).ToArray()
            : [];
    private string ToolContributionState(IReadOnlyList<ToolResource> tools) => tools.Count switch
    {
        0 => "NotMaterialized",
        > 1 => "Conflict",
        _ => item?.AvailabilityStatus == "available" && item.RegistrationEnabled
            && tools[0].Definition.Enabled && tools[0].Definition.Discovery?.Available == true ? "Available" : "Unavailable"
    };
    private IReadOnlyList<ExtensionManagedResourceResponse> MaterializedResources(ExtensionContributionResponse contribution) =>
        string.Equals(contribution.Kind, "data-source-profile-bundle", StringComparison.Ordinal)
            ? (item?.Extension?.BundleInstallations ?? [])
                .Where(installation => string.Equals(installation.BundleId, contribution.Id, StringComparison.Ordinal))
                .Where(installation => installation.State != ExtensionBundleInstallationState.Degraded)
                .SelectMany(installation => installation.ManagedResources)
                .Where(resource => resource.Kind is ToolResourceKinds.ToolSet or FlowResourceKinds.Flow or DataSourceResourceKinds.DataSourceProfile)
                .DistinctBy(resource => (resource.Kind, resource.Namespace, resource.Name, resource.ScopeRef))
                .OrderBy(resource => ManagedResourceKindOrder(resource.Kind))
                .ThenBy(resource => resource.Name, StringComparer.Ordinal)
                .ToArray()
            : [];
    private string BundleContributionState(ExtensionContributionResponse contribution)
    {
        var installations = (item?.Extension?.BundleInstallations ?? [])
            .Where(value => value.BundleId == contribution.Id).ToArray();
        if (installations.Length == 0) return "NotMaterialized";
        if (installations.Any(value => value.State == ExtensionBundleInstallationState.Degraded)) return "Conflict";
        foreach (var installation in installations)
            foreach (var binding in installation.ToolBindings)
            {
                var tools = extensionTools.Where(value => value.Definition.ExternalId == binding.Key).ToArray();
                if (tools.Length > 1) return "Conflict";
                if (tools.Length == 0 || tools[0].Name != binding.Value.Name
                    || tools[0].Namespace != (binding.Value.Namespace ?? ResourceNamespace.Default)
                    || tools[0].ScopeRef != (binding.Value.ScopeRef ?? installation.ScopeRef)
                    || !tools[0].Definition.Enabled || tools[0].Definition.Discovery?.Available != true) return "Unavailable";
            }
        return installations.All(value => value.State == ExtensionBundleInstallationState.Installed)
            && item?.AvailabilityStatus == "available" && item.RegistrationEnabled ? "Available" : "Unavailable";
    }
    private string ContributionKindLabel(string kind)
    {
        var localized = Localizer[$"ContributionKind.{kind}"];
        return localized.ResourceNotFound ? kind : localized.Value;
    }
    private string ContributionDescription(ExtensionContributionResponse contribution, int resourceCount) =>
        contribution.Kind == "data-source-profile-bundle" && BundleContributionState(contribution) == "Conflict"
            ? T("ContributionState.Conflict") : resourceCount > 0
            ? T("ContributionMaterialized", resourceCount)
            : T(string.Equals(contribution.Kind, "data-source-profile-bundle", StringComparison.Ordinal) ? "ContributionAwaitingMaterialization" : "ContributionDeclared");
    private string ManagedResourceKindLabel(string kind)
    {
        var value = Localizer[$"ManagedResourceKind.{kind}"];
        return value.ResourceNotFound ? kind : value.Value;
    }
    private static int ManagedResourceKindOrder(string kind) => kind switch
    {
        ToolResourceKinds.ToolSet => 0,
        DataSourceResourceKinds.DataSourceProfile => 1,
        FlowResourceKinds.Flow => 2,
        _ => 3
    };
    private string ToolUrl(ToolResource tool) => ToolReferenceUrl(tool.ScopeRef ?? ResourceScopeRef.Instance, new ResourceReference(tool.Name, tool.ScopeRef, tool.Namespace)) + "#overview";
    private static string ToolProviderUrl(ToolProviderResource provider) => $"/tools/providers/{Uri.EscapeDataString(provider.Name)}?namespace={Uri.EscapeDataString(provider.Namespace.Value)}&scopeRef={Uri.EscapeDataString((provider.ScopeRef ?? ResourceScopeRef.Instance).Value)}&context={Uri.EscapeDataString((provider.ScopeRef ?? ResourceScopeRef.Instance).Value)}";
    private string ToolReferenceUrl(ResourceScopeRef scopeRef, ResourceReference reference)
    {
        var @namespace = reference.Namespace ?? ResourceNamespace.Default;
        return ConsoleResourceUrls.WithOwnerScope(ResourceOwnershipContext.PreserveNavigation($"/tools/{Uri.EscapeDataString(reference.Name)}?namespace={Uri.EscapeDataString(@namespace.Value)}", Services), reference.ScopeRef ?? scopeRef);
    }
    private static UiStatus InstallationStatus(ExtensionBundleInstallationState state) => state switch
    {
        ExtensionBundleInstallationState.Installed => UiStatus.Success,
        ExtensionBundleInstallationState.Installing or ExtensionBundleInstallationState.Degraded => UiStatus.Warning,
        ExtensionBundleInstallationState.Failed => UiStatus.Danger,
        _ => UiStatus.Neutral
    };
    private string? ManagedResourceUrl(ExtensionManagedResourceResponse resource)
    {
        var name = Uri.EscapeDataString(resource.Name.Split('@')[0]);
        var ns = Uri.EscapeDataString(resource.Namespace);
        var path = resource.Kind switch
        {
            ToolResourceKinds.ToolSet => $"/tools/sets/{name}?namespace={ns}",
            FlowResourceKinds.Flow => resource.Namespace == ResourceNamespace.DefaultValue
                ? $"/flows/{name}" : $"/namespaces/{ns}/flows/{name}",
            DataSourceResourceKinds.DataSourceProfile => $"/data-source-profiles/{name}?namespace={ns}",
            _ => null
        };
        return path is null ? null : ConsoleResourceUrls.WithOwnerScope(ResourceOwnershipContext.PreserveNavigation(path, Services), resource.ScopeRef) + "#overview";
    }
    public void Dispose() { disposed = true; ++loadVersion; if (contextState is not null) contextState.Changed -= ContextChanged; cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
