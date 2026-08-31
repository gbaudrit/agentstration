using System.Text.Json;
using Agentstration.Application;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
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


    private readonly CancellationTokenSource cancellation = new();
    private IReadOnlyList<ExtensionResponse>? extensions;
    private IReadOnlyList<ExtensionRegistrationResource>? registrations;
    private AgentstrationApiException? error;
    private bool loading;
    private bool discovering;
    private string? discoveryMessage;
    private bool saving;
    private bool editing;
    private bool creating;
    private string? etag;
    private RegistrationForm form = new();
    private ExtensionRegistrationResource? pendingDelete;
    private ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>? migrationPreview;
    private bool migrating;
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    private string StatusLabel(string status) => T($"Status.{status}");

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        loading = true;
        error = null;
        try
        {
            var extensionsTask = Client.GetExtensionsAsync(cancellation.Token);
            var registrationsTask = Client.GetRegistrationsAsync(cancellation.Token);
            await Task.WhenAll(extensionsTask, registrationsTask);
            extensions = await extensionsTask;
            registrations = await registrationsTask;
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { loading = false; }
    }

    private async Task DiscoverAsync()
    {
        discovering = true;
        error = null;
        discoveryMessage = null;
        try
        {
            var result = await Client.DiscoverAsync(cancellation.Token);
            await LoadAsync();
            var available = extensions?.Count(value => value.Status == "available") ?? 0;
            discoveryMessage = result.Sources == 0
                ? T("DiscoveryNoSource")
                : T("DiscoveryCompleted", result.Sources, result.Created, result.Updated, result.Unchanged, available);
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { discovering = false; }
    }

    private void StartCreate()
    {
        form = new();
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
            var properties = new ExtensionRegistrationProperties { DisplayName = form.DisplayName, Endpoint = endpoint, Enabled = form.Enabled, ExpectedExtensionId = form.ExpectedExtensionId };
            if (creating)
                _ = await Client.CreateRegistrationAsync(new(form.Name, properties, form.Namespace), cancellation.Token);
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

        public static RegistrationForm From(ExtensionRegistrationResource resource) => new()
        {
            Name = resource.Name,
            Namespace = resource.Namespace.Value,
            DisplayName = resource.Definition.DisplayName,
            Endpoint = resource.Definition.Endpoint.AbsoluteUri,
            Enabled = resource.Definition.Enabled,
            ExpectedExtensionId = resource.Definition.ExpectedExtensionId
        };
    }
}
