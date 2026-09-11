using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
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

public partial class Extensions
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions IndentedWebJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private ExtensionTab activeTab = ExtensionTab.Summary;

    private readonly CancellationTokenSource cancellation = new();
    private Task? initialLoad;
    private bool disposed;
    private IReadOnlyList<ExtensionInventoryItemResponse>? inventory;
    private IReadOnlyList<ExtensionRegistrationResource>? registrations;
    private IReadOnlyList<AepEnrollmentRequestResource>? enrollments;
    private IReadOnlyList<SourceProviderSummaryResponse> sourceProviders = [];
    private AepEnrollmentSettingsSnapshot? enrollmentSettings;
    private bool pairingCodeEnabled;
    private bool sharedKeyFileEnabled;
    private bool savingEnrollmentSettings;
    private AgentstrationApiException? error;
    private bool loading;
    private bool saving;
    private bool editing;
    private bool creating;
    private string? etag;
    private RegistrationForm form = new();
    private ResourceScopeRef? selectedScope;
    private ExtensionRegistrationResource? pendingDelete;
    private ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>? migrationPreview;
    private bool migrating;
    private bool pairingBusy;
    private string? activePairingCode;
    private DateTimeOffset? activePairingExpiry;
    private Uri? activePairingUri;
    private Guid? activeEnrollmentId;
    private bool clipboardFallback;
    private bool refreshingEnrollmentState;
    private AepEnrollmentRequestResource? pendingUnenrollment;
    private readonly Dictionary<Guid, ResourceScopeRef?> enrollmentScopes = [];
    [Parameter, SupplyParameterFromQuery(Name = "tab")]
    public string? RequestedTab { get; set; }
    private string search = string.Empty;
    private string availabilityFilter = string.Empty;
    private AepEnrollmentState? enrollmentFilter;
    private string sourceFilter = string.Empty;
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private string StatusLabel(string status) => T($"Status.{status}");
    private string EnrollmentStatusLabel(AepEnrollmentState status) => T($"EnrollmentStatus.{status}");

    private bool CanAdministerEnrollments => Services.GetService(typeof(ConsoleContextState)) is ConsoleContextState state
        && state.HasPermission(AuthorizationPermissions.ResourcesWrite);
    private int AvailableExtensionCount => inventory?.Count(value => string.Equals(value.AvailabilityStatus, "available", StringComparison.OrdinalIgnoreCase)) ?? 0;
    private int EnabledRegistrationCount => registrations?.Count(value => value.Definition.Enabled) ?? 0;
    private int ActiveEnrollmentCount => enrollments?.Count(value => value.Definition.State is AepEnrollmentState.Pending
        or AepEnrollmentState.Unpaired or AepEnrollmentState.CodeIssued or AepEnrollmentState.CredentialIssued or AepEnrollmentState.Verifying) ?? 0;
    private int EnabledEnrollmentModeCount => (pairingCodeEnabled ? 1 : 0) + (sharedKeyFileEnabled ? 1 : 0);
    private IReadOnlyList<ExtensionInventoryItemResponse> FilteredInventory => (inventory ?? [])
        .Where(value => string.IsNullOrWhiteSpace(search)
            || value.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || value.ExtensionId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (value.RegistrationName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || value.Endpoint.AbsoluteUri.Contains(search, StringComparison.OrdinalIgnoreCase))
        .Where(value => string.IsNullOrWhiteSpace(availabilityFilter)
            || string.Equals(value.AvailabilityStatus, availabilityFilter, StringComparison.OrdinalIgnoreCase))
        .Where(value => enrollmentFilter is null || value.EnrollmentStatus == enrollmentFilter)
        .Where(value => string.IsNullOrWhiteSpace(sourceFilter)
            || string.Equals(value.RegistrationSource, sourceFilter, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    private IReadOnlyList<string> AvailabilityFilters => (inventory ?? []).Select(value => value.AvailabilityStatus).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    private IReadOnlyList<AepEnrollmentState> EnrollmentFilters => (inventory ?? []).Where(value => value.EnrollmentStatus.HasValue).Select(value => value.EnrollmentStatus!.Value).Distinct().Order().ToArray();
    private IReadOnlyList<string> SourceFilters => (inventory ?? []).Select(value => value.RegistrationSource).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    protected override void OnInitialized()
    {
        _ = RefreshCountdownAsync();
        initialLoad = LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || initialLoad is null)
            return;

        try
        {
            await initialLoad;
            if (!disposed)
                await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    protected override void OnParametersSet()
    {
        activeTab = RequestedTab?.ToLowerInvariant() switch
        {
            "catalog" => ExtensionTab.Catalog,
            "enrollments" when CanAdministerEnrollments => ExtensionTab.Enrollments,
            _ => ExtensionTab.Summary
        };
    }

    private async Task LoadAsync()
    {
        loading = true;
        error = null;
        try
        {
            var inventoryTask = Client.GetExtensionInventoryAsync(cancellation.Token);
            var registrationsTask = Client.GetRegistrationsAsync(cancellation.Token);
            var enrollmentsTask = CanAdministerEnrollments ? Client.GetEnrollmentsAsync(cancellation.Token) : Task.FromResult<IReadOnlyList<AepEnrollmentRequestResource>>([]);
            var enrollmentSettingsTask = CanAdministerEnrollments
                ? GetEnrollmentSettingsAsync()
                : Task.FromResult<AepEnrollmentSettingsSnapshot?>(null);
            var sourceProvidersClient = Services.GetService(typeof(ISourceProvidersClient)) as ISourceProvidersClient;
            var sourceProvidersTask = sourceProvidersClient?.GetSourceProvidersAsync(cancellation.Token)
                ?? Task.FromResult<IReadOnlyList<SourceProviderSummaryResponse>>([]);
            await Task.WhenAll(inventoryTask, registrationsTask, enrollmentsTask, enrollmentSettingsTask, sourceProvidersTask);
            inventory = await inventoryTask;
            registrations = await registrationsTask;
            enrollments = await enrollmentsTask;
            enrollmentSettings = await enrollmentSettingsTask;
            sourceProviders = await sourceProvidersTask;
            if (enrollmentSettings is { } settings)
            {
                pairingCodeEnabled = settings.PairingCodeEnabled;
                sharedKeyFileEnabled = settings.SharedKeyFileEnabled;
            }
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { loading = false; }
    }

    private async Task<AepEnrollmentSettingsSnapshot?> GetEnrollmentSettingsAsync() =>
        await Client.GetEnrollmentSettingsAsync(cancellation.Token);

    private async Task SaveEnrollmentSettingsAsync()
    {
        savingEnrollmentSettings = true;
        error = null;
        try
        {
            enrollmentSettings = await Client.UpdateEnrollmentSettingsAsync(
                pairingCodeEnabled,
                sharedKeyFileEnabled,
                enrollmentSettings?.ETag,
                cancellation.Token);
            pairingCodeEnabled = enrollmentSettings.PairingCodeEnabled;
            sharedKeyFileEnabled = enrollmentSettings.SharedKeyFileEnabled;
            Notifications.Add(new NotificationItem(
                Guid.NewGuid(),
                T("EnrollmentModesSaved"),
                T("EnrollmentModes.Description"),
                DateTimeOffset.Now,
                UiStatus.Success));
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { savingEnrollmentSettings = false; }
    }

    private async Task EnrollAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        error = null;
        try
        {
            if (!await AssignIfRequiredAsync(enrollment)) return;
            var result = await Client.RotateEnrollmentCodeAsync(enrollment.Definition.InstanceId, cancellation.Token);
            var pairingUri = enrollment.Definition.PairingUri
                ?? throw new InvalidOperationException("A PairingCode enrollment has no pairing URI.");
            activePairingCode = result.Code;
            activePairingExpiry = result.ExpiresAt;
            activePairingUri = pairingUri;
            activeEnrollmentId = enrollment.Definition.InstanceId;
            var opened = await JavaScript.InvokeAsync<bool>(
                "agentstrationEnrollment.postAndOpen",
                cancellation.Token,
                result.Code,
                pairingUri.AbsoluteUri);
            clipboardFallback = !opened;
            if (opened)
            {
                activePairingCode = null;
                activePairingExpiry = null;
                activePairingUri = null;
            }
            await LoadAsync();
        }
        catch (JSException) { clipboardFallback = true; }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private ResourceScopeRef? EnrollmentScope(AepEnrollmentRequestResource enrollment) =>
        enrollmentScopes.TryGetValue(enrollment.Definition.InstanceId, out var scope)
            ? scope
            : enrollment.Definition.TargetScopeRef;

    private void SetEnrollmentScope(AepEnrollmentRequestResource enrollment, ResourceScopeRef? scope) =>
        enrollmentScopes[enrollment.Definition.InstanceId] = scope;

    private async Task<bool> AssignIfRequiredAsync(AepEnrollmentRequestResource enrollment)
    {
        if (enrollment.Definition.TargetScopeRef is not null && enrollment.Definition.State != AepEnrollmentState.Unpaired) return true;
        var scope = EnrollmentScope(enrollment);
        if (scope is null) return false;
        await Client.AssignEnrollmentAsync(enrollment.Definition.InstanceId, scope.Value, cancellation.Token);
        return true;
    }

    private async Task AssignSharedKeyAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        error = null;
        try
        {
            var scope = EnrollmentScope(enrollment);
            if (scope is null) return;
            await Client.AssignEnrollmentAsync(enrollment.Definition.InstanceId, scope.Value, cancellation.Token);
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private async Task CopyCodeAsync()
    {
        if (activePairingCode is null) return;
        try { await JavaScript.InvokeVoidAsync("agentstrationEnrollment.copy", cancellation.Token, activePairingCode); clipboardFallback = false; }
        catch (JSException) { clipboardFallback = true; }
    }

    private async Task OpenExtension()
    {
        if (activePairingUri is not null) await JavaScript.InvokeVoidAsync("agentstrationEnrollment.open", activePairingUri.AbsoluteUri);
    }

    private async Task RejectAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        try { await Client.RejectEnrollmentAsync(enrollment.Definition.InstanceId, cancellation.Token); await LoadAsync(); }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private async Task CancelEnrollmentAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        try { await Client.CancelEnrollmentAsync(enrollment.Definition.InstanceId, cancellation.Token); await LoadAsync(); }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private async Task RotateCredentialAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        try { await Client.RotateEnrollmentCredentialAsync(enrollment.Definition.InstanceId, cancellation.Token); await LoadAsync(); }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private async Task RevokeCredentialAsync(AepEnrollmentRequestResource enrollment)
    {
        pairingBusy = true;
        try { await Client.RevokeEnrollmentCredentialAsync(enrollment.Definition.InstanceId, cancellation.Token); await LoadAsync(); }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { pairingBusy = false; }
    }

    private void RequestUnenrollment(AepEnrollmentRequestResource enrollment) => pendingUnenrollment = enrollment;
    private void CancelUnenrollment() => pendingUnenrollment = null;

    private async Task UnenrollAsync()
    {
        var enrollment = pendingUnenrollment;
        if (enrollment is null) return;
        pairingBusy = true;
        try
        {
            await Client.UnenrollExtensionAsync(enrollment.Definition.InstanceId, cancellation.Token);
            pendingUnenrollment = null;
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("ExtensionUnenrolled"), T("ExtensionUnenrolled.Description"), DateTimeOffset.Now, UiStatus.Success));
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; pendingUnenrollment = null; }
        finally { pairingBusy = false; }
    }

    private async Task RefreshCountdownAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await InvokeAsync(StateHasChanged);
                if (++tick % 2 == 0 && activeEnrollmentId is not null)
                    await InvokeAsync(RefreshEnrollmentStateAsync);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task RefreshEnrollmentStateAsync()
    {
        if (refreshingEnrollmentState || pairingBusy || loading || !CanAdministerEnrollments) return;
        refreshingEnrollmentState = true;
        try
        {
            var refreshed = await Client.GetEnrollmentsAsync(cancellation.Token);
            var active = activeEnrollmentId is { } instanceId
                ? refreshed.SingleOrDefault(value => value.Definition.InstanceId == instanceId)
                : null;
            var changed = enrollments is null
                || refreshed.Count != enrollments.Count
                || refreshed.Any(value => enrollments.SingleOrDefault(current => current.Definition.InstanceId == value.Definition.InstanceId)?.Definition.State != value.Definition.State);
            enrollments = refreshed;
            if (active is null || active.Definition.State != AepEnrollmentState.CodeIssued)
            {
                activePairingCode = null;
                activePairingExpiry = null;
                activePairingUri = null;
                activeEnrollmentId = null;
            }
            if (changed)
            {
                var inventoryTask = Client.GetExtensionInventoryAsync(cancellation.Token);
                var registrationsTask = Client.GetRegistrationsAsync(cancellation.Token);
                await Task.WhenAll(inventoryTask, registrationsTask);
                inventory = await inventoryTask;
                registrations = await registrationsTask;
            }
        }
        catch (AgentstrationApiException)
        {
            // A transient polling failure must not replace the last usable screen state.
        }
        finally { refreshingEnrollmentState = false; }
    }

    private static bool CanRotate(AepEnrollmentState state) => state is AepEnrollmentState.Pending
        or AepEnrollmentState.CodeIssued or AepEnrollmentState.Expired or AepEnrollmentState.AttemptsExceeded
        or AepEnrollmentState.VerificationFailed or AepEnrollmentState.Unpaired;
    private static bool CanUnenroll(AepEnrollmentState state) => state is AepEnrollmentState.Available
        or AepEnrollmentState.Disabled or AepEnrollmentState.VerificationFailed or AepEnrollmentState.Unenrolling;
    private static UiStatus EnrollmentStatus(AepEnrollmentState state) => state switch
    {
        AepEnrollmentState.Available => UiStatus.Success,
        AepEnrollmentState.Disabled => UiStatus.Neutral,
        AepEnrollmentState.Rejected or AepEnrollmentState.Cancelled or AepEnrollmentState.Expired or AepEnrollmentState.AttemptsExceeded or AepEnrollmentState.VerificationFailed or AepEnrollmentState.Revoked => UiStatus.Danger,
        AepEnrollmentState.CodeIssued or AepEnrollmentState.CredentialIssued or AepEnrollmentState.Verifying or AepEnrollmentState.Unenrolling => UiStatus.Warning,
        _ => UiStatus.Neutral
    };
    private static long SecondsRemaining(DateTimeOffset expires) => Math.Max(0, (long)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalSeconds));
    private static string EnrollmentAge(AepEnrollmentRequestResource request) =>
        $"{Math.Max(0, (long)(DateTimeOffset.UtcNow - request.Definition.AnnouncedAt).TotalMinutes)}m";

    private void StartCreate()
    {
        activeTab = ExtensionTab.Catalog;
        form = new();
        selectedScope = null;
        etag = null;
        creating = true;
        editing = true;
    }

    private async Task EditAsync(ExtensionRegistrationResource registration)
    {
        try
        {
            var snapshot = await Client.GetRegistrationAsync(registration.Namespace, registration.Name, cancellation.Token);
            form = RegistrationForm.From(snapshot.Value);
            selectedScope = snapshot.Value.ScopeRef;
            etag = snapshot.ETag;
            creating = false;
            editing = true;
        }
        catch (AgentstrationApiException exception) { error = exception; }
    }

    private async Task SaveAsync()
    {
        saving = true;
        error = null;
        try
        {
            if (!Uri.TryCreate(form.Endpoint, UriKind.Absolute, out var endpoint))
                throw new AgentstrationApiException(T("EndpointMustBeAbsolute"), Guid.NewGuid().ToString("N"));
            ResourceReference? credential = null;
            if (form.AuthenticationMode == AepTransportAuthenticationMode.StaticBearer)
            {
                if (string.IsNullOrWhiteSpace(form.CredentialName))
                    throw new AgentstrationApiException(T("CredentialRequired"), Guid.NewGuid().ToString("N"));
                ResourceScopeRef? credentialScope = string.IsNullOrWhiteSpace(form.CredentialScopeRef)
                    ? null
                    : ResourceScopeRef.Parse(form.CredentialScopeRef);
                credential = new(form.CredentialName, credentialScope, ResourceNamespace.Parse(form.CredentialNamespace));
            }
            var properties = new ExtensionRegistrationProperties
            {
                DisplayName = form.DisplayName,
                Endpoint = endpoint,
                Enabled = form.Enabled,
                ExpectedExtensionId = form.ExpectedExtensionId,
                AuthenticationMode = form.AuthenticationMode,
                Credential = credential
            };
            if (creating)
                _ = await Client.CreateRegistrationAsync(new(form.Name, properties, form.Namespace, selectedScope), cancellation.Token);
            else
                _ = await Client.UpdateRegistrationAsync(ResourceNamespace.Parse(form.Namespace), form.Name, new(properties), etag!, cancellation.Token);
            editing = false;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { saving = false; }
    }

    private async Task ToggleAsync(ExtensionRegistrationResource registration)
    {
        try
        {
            var snapshot = await Client.GetRegistrationAsync(registration.Namespace, registration.Name, cancellation.Token);
            _ = await Client.UpdateRegistrationAsync(registration.Namespace, registration.Name,
                new(snapshot.Value.Definition with { Enabled = !snapshot.Value.Definition.Enabled }), snapshot.ETag, cancellation.Token);
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
    }

    private void RequestDelete(ExtensionRegistrationResource registration) => pendingDelete = registration;
    private void CancelDelete() => pendingDelete = null;
    private string DeleteMessage => T("DeleteRegistrationMessage", pendingDelete?.Namespace.ToString() ?? string.Empty, pendingDelete?.Name ?? string.Empty);

    private async Task DeleteAsync()
    {
        var registration = pendingDelete;
        if (registration is null) return;
        try
        {
            var snapshot = await Client.GetRegistrationAsync(registration.Namespace, registration.Name, cancellation.Token);
            await Client.DeleteRegistrationAsync(registration.Namespace, registration.Name, snapshot.ETag, cancellation.Token);
            pendingDelete = null;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
    }

    private void CancelEdit() => editing = false;

    private string SourceLabel(string source) => T($"Source.{source}");
    private static string ContributionSummary(ExtensionInventoryItemResponse item) => item.Extension?.Contributions.Count > 0
        ? string.Join(", ", item.Extension.Contributions.Select(value => $"{value.Kind}:{value.Id}"))
        : "—";
    private IReadOnlyList<SourceProviderSummaryResponse> SourceProviders(ExtensionInventoryItemResponse item) =>
        item.RegistrationName is null
            ? []
            : sourceProviders.Where(provider =>
                provider.ExtensionNamespace == item.RegistrationNamespace
                && provider.ExtensionName == item.RegistrationName
                && provider.ExtensionScopeRef == item.RegistrationScopeRef).ToArray();
    private static string SourceProviderUrl(SourceProviderSummaryResponse provider) =>
        $"/sourceproviders/{Uri.EscapeDataString(provider.Name)}?namespace={Uri.EscapeDataString(provider.Namespace)}&scopeRef={Uri.EscapeDataString(provider.ScopeRef!.Value.Value)}";
    private static string ConfigureSourceProviderUrl(ExtensionInventoryItemResponse item, ExtensionContributionResponse contribution) =>
        $"/sourceproviders/new?extension={Uri.EscapeDataString(item.RegistrationName!)}&extensionNamespace={Uri.EscapeDataString(item.RegistrationNamespace)}&extensionScopeRef={Uri.EscapeDataString(item.RegistrationScopeRef!.Value.Value)}&contributionId={Uri.EscapeDataString(contribution.Id)}&displayName={Uri.EscapeDataString(item.DisplayName)}";
    private static string DetailUrl(ExtensionInventoryItemResponse item) => item.RegistrationName is not null
        ? $"/extensions/{Uri.EscapeDataString(item.RegistrationName)}?namespace={Uri.EscapeDataString(item.RegistrationNamespace)}"
        : $"/extensions/enrollment/{item.EnrollmentInstanceId:D}";
    private string EnrollmentDetailUrl(AepEnrollmentRequestResource enrollment) => inventory?
        .Where(value => value.EnrollmentInstanceId == enrollment.Definition.InstanceId)
        .Select(DetailUrl)
        .SingleOrDefault() ?? $"/extensions/enrollment/{enrollment.Definition.InstanceId:D}";

    private async Task PreviewMigrationAsync(ExtensionOptionUsageResponse usage, string targetVersion)
    {
        migrating = true;
        error = null;
        try
        {
            migrationPreview = await ProfilesClient.PreviewOptionMigrationAsync(
                ResourceNamespace.Parse(usage.ProfileNamespace),
                usage.ProfileName,
                targetVersion,
                cancellation.Token);
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { migrating = false; }
    }

    private async Task ApplyMigrationAsync()
    {
        var preview = migrationPreview;
        if (preview is null || migrating) return;
        migrating = true;
        error = null;
        try
        {
            _ = await ProfilesClient.ApplyOptionMigrationAsync(
                ResourceNamespace.Parse(preview.Value.ProfileNamespace),
                preview.Value.ProfileName,
                preview.Value.Target.Version,
                preview.ETag,
                cancellation.Token);
            Notifications.Add(new NotificationItem(
                Guid.NewGuid(),
                T("ExtensionOptionsMigrated"),
                $"{preview.Value.ProfileName}: {preview.Value.Source.Version} → {preview.Value.Target.Version}",
                DateTimeOffset.Now,
                UiStatus.Success));
            migrationPreview = null;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { migrating = false; }
    }

    private void CancelMigration() => migrationPreview = null;

    private static string ShortDigest(string value) => value.Length <= 24 ? value : $"{value[..18]}…{value[^6..]}";
    private static string FormatSchema(System.Text.Json.JsonElement schema) =>
        JsonSerializer.Serialize(schema, IndentedJsonOptions);
    private static string FormatOptions(VersionedExtensionOptions options) =>
        JsonSerializer.Serialize(options, IndentedWebJsonOptions);
    private static string? MigrationTarget(ExtensionResponse extension, ExtensionOptionUsageResponse usage)
    {
        var optionSet = extension.OptionSets.SingleOrDefault(value => string.Equals(value.Id, usage.OptionSet, StringComparison.Ordinal));
        if (optionSet is null || string.Equals(optionSet.PreferredVersion, usage.Version, StringComparison.Ordinal)) return null;
        return HasMigrationPath(optionSet, usage.Version, optionSet.PreferredVersion) ? optionSet.PreferredVersion : null;
    }
    private static bool HasMigrationPath(ExtensionOptionSetResponse optionSet, string source, string target)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { source };
        pending.Enqueue(source);
        while (pending.TryDequeue(out var current))
        {
            foreach (var migration in optionSet.Migrations.Where(value => string.Equals(value.FromVersion, current, StringComparison.Ordinal)))
            {
                if (string.Equals(migration.ToVersion, target, StringComparison.Ordinal)) return true;
                if (visited.Add(migration.ToVersion)) pending.Enqueue(migration.ToVersion);
            }
        }
        return false;
    }
    private static string ProfileUrl(ExtensionOptionUsageResponse usage) =>
        $"/modelprofiles/{Uri.EscapeDataString(usage.ProfileName)}?namespace={Uri.EscapeDataString(usage.ProfileNamespace)}";
    private static string ProviderUrl(ExtensionProviderBindingResponse provider) =>
        $"/modelproviders/{Uri.EscapeDataString(provider.Name)}?namespace={Uri.EscapeDataString(provider.Namespace)}";
    private static string ConfigureProviderUrl(ExtensionResponse extension, ExtensionContributionResponse contribution) =>
        $"/modelproviders/new?extension={Uri.EscapeDataString(extension.RegistrationName)}&extensionNamespace={Uri.EscapeDataString(extension.RegistrationNamespace)}&contributionId={Uri.EscapeDataString(contribution.Id)}&displayName={Uri.EscapeDataString(extension.Extension?.Name ?? contribution.Id)}";

    public void Dispose()
    {
        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class RegistrationForm
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string Name { get; set; } = string.Empty;
        [System.ComponentModel.DataAnnotations.Required]
        public string Namespace { get; set; } = "default";
        [System.ComponentModel.DataAnnotations.Required]
        public string DisplayName { get; set; } = string.Empty;
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.Url]
        public string Endpoint { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string? ExpectedExtensionId { get; set; }
        public AepTransportAuthenticationMode AuthenticationMode { get; set; }
        public string CredentialName { get; set; } = string.Empty;
        public string CredentialNamespace { get; set; } = ResourceNamespace.DefaultValue;
        public string CredentialScopeRef { get; set; } = string.Empty;

        public static RegistrationForm From(ExtensionRegistrationResource resource) => new()
        {
            Name = resource.Name,
            Namespace = resource.Namespace.Value,
            DisplayName = resource.Definition.DisplayName,
            Endpoint = resource.Definition.Endpoint.AbsoluteUri,
            Enabled = resource.Definition.Enabled,
            ExpectedExtensionId = resource.Definition.ExpectedExtensionId,
            AuthenticationMode = resource.Definition.AuthenticationMode,
            CredentialName = resource.Definition.Credential?.Name ?? string.Empty,
            CredentialNamespace = resource.Definition.Credential?.Namespace?.Value ?? resource.Namespace.Value,
            CredentialScopeRef = resource.Definition.Credential?.ScopeRef?.Value ?? string.Empty
        };
    }

    private enum ExtensionTab { Summary, Catalog, Enrollments }
}
