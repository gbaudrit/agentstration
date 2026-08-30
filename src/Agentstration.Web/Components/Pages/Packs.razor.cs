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

public partial class Packs
{

    [SupplyParameterFromQuery(Name = "publisher")] public string? RequestedPublisher { get; set; }
    [SupplyParameterFromQuery(Name = "name")] public string? RequestedName { get; set; }

    private const long MaximumArchiveBytes = 8 * 1024 * 1024;
    private readonly CancellationTokenSource cancellation = new();
    private IReadOnlyList<InstalledPackResource>? packs;
    private IReadOnlyList<PackProjectResource>? projects;
    private ResourceSnapshot<InstalledPackResource>? selected;
    private AgentstrationApiException? error;
    private PackInstallationPreview? preview;
    private IReadOnlyList<ModelProfileSummaryResponse> modelProfiles = [];
    private IReadOnlyList<ModelProviderResponse> modelProviders = [];
    private IReadOnlyList<RuntimeProfileSummaryResponse> runtimeProfiles = [];
    private IReadOnlyList<ExtensionRegistrationResource> extensionRegistrations = [];
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

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        loading = true;
        error = null;
        try
        {
            packs = await Client.GetPacksAsync(cancellation.Token);
            projects = await Client.GetProjectsAsync(cancellation.Token);
            if (!string.IsNullOrWhiteSpace(RequestedPublisher) && !string.IsNullOrWhiteSpace(RequestedName))
            {
                var requested = packs.FirstOrDefault(pack =>
                    string.Equals(pack.Definition.Publisher, RequestedPublisher, StringComparison.Ordinal)
                    && string.Equals(pack.Definition.PackName, RequestedName, StringComparison.Ordinal));
                selected = requested is null ? null : await Client.GetPackAsync(requested.Definition.Publisher, requested.Definition.PackName, cancellation.Token);
            }
            else if (selected is not null)
            {
                var match = packs.FirstOrDefault(pack => SameIdentity(pack, selected.Value));
                selected = match is null ? null : await Client.GetPackAsync(match.Definition.Publisher, match.Definition.PackName, cancellation.Token);
            }
        }
        catch (AgentstrationApiException exception) { error = exception; }
        finally { loading = false; }
    }

    private void OpenInstall()
    {
        preview = null;
        archive = null;
        archiveName = string.Empty;
        installError = null;
        replaceExisting = false;
        bindingSelections.Clear();
        installOpen = true;
    }

    private void CloseInstall()
    {
        if (installing) return;
        installOpen = false;
    }

    private async Task ReadArchiveAsync(InputFileChangeEventArgs args)
    {
        previewing = true;
        preview = null;
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
                    bindingSelections[binding.Name] = BindingValue(binding.Target.Name, binding.Target.Namespace ?? ResourceNamespace.Default);
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
        error = null;
        try { selected = await Client.GetPackAsync(pack.Definition.Publisher, pack.Definition.PackName, cancellation.Token); }
        catch (AgentstrationApiException exception) { error = exception; }
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
    private static bool SameIdentity(InstalledPackResource left, InstalledPackResource right) => left.Definition.Publisher == right.Definition.Publisher && left.Definition.PackName == right.Definition.PackName;
    private static string DisplayName(InstalledPackResource pack) => pack.Definition.DisplayName ?? pack.Definition.PackName;
    private string? SelectedBinding(string name) => bindingSelections.GetValueOrDefault(name);
    private void BindingChanged(string name, ChangeEventArgs args) => bindingSelections[name] = args.Value?.ToString() ?? string.Empty;
    private static string BindingValue(string name, ResourceNamespace @namespace) => $"{@namespace.Value}:{name}";
    private static string? DefinitionFormUrl(ManagedPackResource resource) => resource.Kind switch
    {
        ResourceKinds.Agent => $"/namespaces/{Uri.EscapeDataString(resource.Namespace.Value)}/agents/{Uri.EscapeDataString(resource.Name)}?view=definition",
        ResourceKinds.Flow => $"/namespaces/{Uri.EscapeDataString(resource.Namespace.Value)}/flows/{Uri.EscapeDataString(resource.Name)}?view=definition",
        ResourceKinds.Entry => $"/namespaces/{Uri.EscapeDataString(resource.Namespace.Value)}/entries/{Uri.EscapeDataString(resource.Name)}?view=definition",
        _ => null
    };
    private static string BindingTarget(ResourceReference target) => $"{(target.Namespace ?? ResourceNamespace.Default).Value}/{target.Name}";
    private ResourceReference ParseBindingTarget(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2) throw new InvalidOperationException(T("InvalidPackBinding"));
        return new(parts[1], @namespace: ResourceNamespace.Parse(parts[0]));
    }
    private string BindingKindLabel(PackBindingTargetKind kind) => kind switch
    {
        PackBindingTargetKind.Secret => T("Binding.Secret"),
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
        cancellation.Cancel();
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
