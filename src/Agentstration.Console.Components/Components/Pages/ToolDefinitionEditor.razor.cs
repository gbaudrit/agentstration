using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.ResourceManagement;
using Agentstration.Tools;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class ToolDefinitionEditor
{
    [Parameter] public string? Name { get; set; }
    [SupplyParameterFromQuery(Name = "namespace")] public string? NamespaceQuery { get; set; }
    [SupplyParameterFromQuery(Name = "scopeRef")] public string? ScopeRefValue { get; set; }
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private readonly CancellationTokenSource cancellation = new();
    private readonly ResourceSectionState sectionState = new();
    private ResourceSnapshot<ToolDefinitionResource>? snapshot, newerSnapshot;
    private ToolDefinitionResource? resource;
    private AcquisitionToolDefinitionProperties? acquisitionDefinition;
    private ToolDefinitionEditorModel model = new();
    private IReadOnlyList<FlowSummary> flows = [];
    private IReadOnlyList<ToolDefinitionVersionResource> versions = [];
    private IReadOnlyList<ToolDefinitionUsage> usages = [];
    private string inputSchema = "{}", outputSchema = "", loadedContractVersion = "", savedFingerprint = "", savedFlowSelection = "";
    private ConsoleContextState? contextState;
    private bool subscribed;
    private bool loading, saving, loadingContract, contractLoaded, invalidInput, confirmDelete, historyLoading, historyLoaded, disposed;
    private string? loadedKey, error, historyError;
    private long loadVersion, contractVersion;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private EditContext formContext;
    private ValidationMessageStore messages;
    private ResourceDefinitionForm? definitionForm;
    private string BackUrl => ResourceOwnershipContext.PreserveNavigation("/tools/definitions", Services);
    private bool CanManage => resource?.ScopeRef is { } owner && ResourceOwnershipContext.CanManage(owner, Services);
    private bool HasPermission(string permission) => contextState is null || contextState.Current is not null && contextState.HasPermission(permission);
    private bool CanMutate => !disposed && CanManage && resource?.Governance.IsManaged == false && HasPermission("resources/write");
    private ResourceReadOnlyReason? DefinitionReadOnlyReason => !HasPermission("resources/write") ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private string Fingerprint => JsonSerializer.Serialize(model);
    private string FlowSelection => JsonSerializer.Serialize(new { model.SelectedFlow, model.UseActiveVersion, model.FlowVersion });
    private bool IsDirty => resource?.Definition is not RelayToolDefinitionProperties && resource is not null && savedFingerprint.Length > 0 && (Fingerprint != savedFingerprint || invalidInput);
    private string T(string key, params object[] arguments) => Localizer[key, arguments];
    private ResourceAddress? RequestedAddress => string.IsNullOrWhiteSpace(Name) ? null : ResourceAddress.Create(ResourceNamespace.Parse(NamespaceQuery), ToolResourceKinds.ToolDefinition, Name);
    private string SavedYaml => resource is null ? string.Empty : ResourceManifestSerializer.ToYaml(resource);
    private string AddressKey => $"{Name}|{NamespaceQuery}|{ScopeRefValue}|{ResourceOwnershipContext.TryGetCurrent(Services)}|{contextState?.Current?.UserId}";
    private IReadOnlyDictionary<string, ResourceSectionPresentation> Presentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { ReadOnly = !CanMutate, HasRequiredFields = true, Fields = [nameof(model.Name), nameof(model.DisplayName), nameof(model.Namespace), nameof(model.Description), nameof(model.Tags)] },
        ["definition/configuration"] = new() { ReadOnly = !CanMutate, HasRequiredFields = true, Fields = [nameof(model.RequiresApproval), nameof(model.TimeoutSeconds), nameof(model.PublicationVersion), nameof(model.Publish), nameof(model.Enabled)] },
        ["definition/implementation"] = new() { ReadOnly = !CanMutate, HasRequiredFields = acquisitionDefinition is null, Fields = [nameof(model.SelectedFlow), nameof(model.FlowVersion), nameof(model.UseActiveVersion)] },
        ["yaml"] = new() { ReadOnly = true, ReadOnlyMessage = T("DefinitionYamlHelp") }
    };
    private IReadOnlyDictionary<string, string> FieldLabels => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(model.DisplayName)] = T("DisplayName"), [nameof(model.TimeoutSeconds)] = T("TimeoutSeconds"),
        [nameof(model.PublicationVersion)] = T("RelayVersion")
    };
    private IReadOnlyList<ResourceNavigationItem> DefinitionSections => [new("definition/implementation", T(acquisitionDefinition is null ? "FlowImplementation" : "AcquisitionImplementation"), "sitemap")];
    private IReadOnlyList<ResourceNavigationItem> AdditionalSections => [new("versions", T("RelayVersions"), "history"), new("usages", T("RelayUsages"), "link")];
    private RenderFragment<string> DefinitionContent => path => path switch { "definition/identity" => IdentityFields, "definition/implementation" => ImplementationFields, _ => OptionsFields };
    public ToolDefinitionEditor() { formContext = new(model); messages = new(formContext); }
    private void ShowSection(string path) => Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path);
    private async Task SelectSectionAsync(string? value) { if (value is "versions" or "usages" && !historyLoaded) await LoadHistoryAsync(); }
    protected override void OnInitialized() => contextState = Services.GetService(typeof(ConsoleContextState)) as ConsoleContextState;
    private void ContextChanged() => _ = InvokeAsync(async () =>
    {
        if (disposed) return;
        if (loadedKey != AddressKey) { ++loadVersion; loadedKey = AddressKey; ClearResource(); sectionState.Reset(loadedKey); await LoadAsync(); }
        else if (!HasPermission("resources/read")) { ++loadVersion; ClearResource(); loadState = new(ResourceLoadStatus.Forbidden); }
        StateHasChanged();
    });
    protected override Task OnParametersSetAsync()
    {
        if (AddressKey == loadedKey) return Task.CompletedTask;
        loadedKey = AddressKey; ClearResource(); sectionState.Reset(loadedKey);
        return LoadAsync();
    }
    private Task RefreshAsync() => disposed || saving || loading ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync()
    {
        var request = ++loadVersion; var key = loadedKey; loading = true; error = null;
        loadState = new(resource is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = resource is not null };
        if (string.IsNullOrWhiteSpace(Name)) { loading = false; Navigation.NavigateTo(ResourceOwnershipContext.PreserveNavigation("/tools/definitions/new", Services)); return; }
        try
        {
            if (contextState is not null)
            {
                await contextState.EnsureLoadedAsync(cancellation.Token); if (disposed || request != loadVersion) return;
                if (contextState.Current is null) throw new InvalidOperationException(contextState.Error);
                key = loadedKey = AddressKey;
                if (!subscribed) { contextState.Changed += ContextChanged; subscribed = true; }
                if (!HasPermission("resources/read")) { ClearResource(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            }
            var ns = ResourceNamespace.Parse(NamespaceQuery); var requestedName = Name;
            ResourceScopeRef? owner = string.IsNullOrWhiteSpace(ScopeRefValue) ? snapshot?.Value.ScopeRef : ResourceScopeRef.Parse(ScopeRefValue);
            var saved = owner is { } exact ? await Client.GetAsync(exact, requestedName, ns, cancellation.Token) : await Client.GetAsync(requestedName, ns, cancellation.Token);
            if (!Current(request, key)) return;
            if (!Matches(saved.Value, requestedName, ns, owner, resource?.Uid)) { ClearResource(); loadState = new(ResourceLoadStatus.NotFound); return; }
            if (saved.Value.Definition is RelayToolDefinitionProperties) { snapshot = saved; resource = saved.Value; loadState = new(ResourceLoadStatus.Ready) { HasContent = true }; return; }
            if (IsDirty)
            {
                newerSnapshot = snapshot?.ETag != saved.ETag || JsonSerializer.Serialize(resource) != JsonSerializer.Serialize(saved.Value) ? saved : null;
                loadState = new(ResourceLoadStatus.Ready) { HasContent = true }; return;
            }
            if (saved.Value.Definition is FlowToolDefinitionProperties && saved.Value.ScopeRef is { } sourceContext && !saved.Value.Governance.IsManaged && ResourceOwnershipContext.CanManage(sourceContext, Services))
            {
                var available = await Flows.GetFlowsAsync(sourceContext, cancellation.Token);
                if (!Current(request, key)) return;
                flows = available;
            }
            if (IsDirty) { newerSnapshot = saved; loadState = new(ResourceLoadStatus.Ready) { HasContent = true }; return; }
            ApplySnapshot(saved);
            if (acquisitionDefinition is null && CanMutate && Selected() is not null) await RefreshContractAsync();
            if (!Current(request, key)) return;
            savedFlowSelection = FlowSelection; savedFingerprint = Fingerprint;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!Current(request, key)) return;
            var failure = LoadFailure(exception);
            if (failure.Status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound) ClearResource();
            loadState = failure with { HasContent = resource is not null };
        }
        finally { if (Current(request, key)) loading = false; }
    }
    private bool Current(long request, string? key) => !disposed && request == loadVersion && key == loadedKey && key == AddressKey && (contextState?.Current is null || contextState.HasPermission("resources/read"));
    private static bool Matches(ToolDefinitionResource value, string name, ResourceNamespace ns, ResourceScopeRef? owner, Guid? uid) => value.Kind == ToolResourceKinds.ToolDefinition && value.Name == name && value.Namespace == ns && (owner is null || value.ScopeRef == owner) && (uid is null || value.Uid == uid);
    private void ClearResource()
    {
        contractVersion++; resource = null; snapshot = newerSnapshot = null; acquisitionDefinition = null; error = historyError = null;
        flows = []; versions = []; usages = []; historyLoaded = historyLoading = confirmDelete = invalidInput = saving = loading = false;
        savedFingerprint = savedFlowSelection = ""; model = new(); ReplaceContext();
    }
    private void ReplaceContext()
    {
        formContext.OnFieldChanged -= FieldChanged; formContext = new(model); messages = new(formContext); formContext.OnFieldChanged += FieldChanged;
    }
    private void ApplySnapshot(ResourceSnapshot<ToolDefinitionResource> saved)
    {
        snapshot = saved; resource = saved.Value; newerSnapshot = null; model = ToolDefinitionEditorModel.From(resource);
        acquisitionDefinition = resource.Definition as AcquisitionToolDefinitionProperties;
        if (resource.Definition is FlowToolDefinitionProperties flow) model.SelectedFlow = ExistingFlowKey(flow.Flow, resource.Namespace);
        inputSchema = Pretty(resource.Definition.InputSchema); outputSchema = resource.Definition.OutputSchema is { } output ? Pretty(output) : "";
        loadedContractVersion = model.FlowVersion; contractLoaded = false; invalidInput = false; ReplaceContext();
        savedFlowSelection = FlowSelection; savedFingerprint = Fingerprint;
    }
    private void FieldChanged(object? sender, FieldChangedEventArgs args) { messages.Clear(args.FieldIdentifier); formContext.NotifyValidationStateChanged(); }
    private async Task FlowSelectionChangedAsync() { model.FlowVersion = Selected()?.ActiveVersion ?? ""; await LoadContractAsync(); }
    private async Task LoadContractAsync() { if (Selected() is null) { ClearContract(); return; } await RefreshContractAsync(); }
    private async Task<bool> RefreshContractAsync()
    {
        var request = ++contractVersion; var contextVersion = loadVersion; var key = loadedKey; loadingContract = true; contractLoaded = false; error = null;
        try
        {
            var flow = Selected() ?? throw new InvalidOperationException(T("SelectFlow"));
            var version = model.UseActiveVersion ? flow.ActiveVersion : model.FlowVersion;
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException(T("FlowVersionRequired"));
            var published = await Flows.GetFlowVersionAsync(flow.Namespace, flow.Id, version, flow.ScopeRef, resource!.ScopeRef!.Value, cancellation.Token);
            if (request != contractVersion || !Current(contextVersion, key)) return false;
            var ambiguous = false; var output = published.Graph?.ResolveSingleSuccessOutputSchema(out ambiguous);
            if (ambiguous) throw new InvalidOperationException(T("FlowOutputContractAmbiguous"));
            inputSchema = published.Graph?.ResolveInputSchema() is { } input ? Pretty(input) : "{}"; outputSchema = output is { } result ? Pretty(result) : "";
            model.FlowVersion = loadedContractVersion = version; contractLoaded = true; return true;
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { return false; }
        catch (Exception exception) { if (request == contractVersion && Current(contextVersion, key)) error = exception.Message; return false; }
        finally { if (request == contractVersion && Current(contextVersion, key)) loadingContract = false; }
    }
    private void ClearContract() { contractVersion++; error = null; loadingContract = contractLoaded = false; inputSchema = "{}"; outputSchema = loadedContractVersion = ""; }
    private async Task SaveAsync()
    {
        if (!CanMutate || saving || loading || resource is null || snapshot is null) return;
        if (invalidInput || !formContext.Validate()) { if (definitionForm is not null) await definitionForm.RevealValidationAsync(); return; }
        var unchanged = FlowSelection == savedFlowSelection;
        if (acquisitionDefinition is null && !unchanged && (Selected() is null || !model.UseActiveVersion && string.IsNullOrWhiteSpace(model.FlowVersion)))
        {
            messages.Add(formContext.Field(nameof(model.SelectedFlow)), T(Selected() is null ? "SelectFlow" : "FlowVersionRequired")); formContext.NotifyValidationStateChanged();
            if (definitionForm is not null) await definitionForm.RevealValidationAsync(); return;
        }
        saving = true; error = null; var request = loadVersion; var key = loadedKey; var original = resource;
        try
        {
            ToolDefinitionProperties properties = original.Definition;
            if (properties is FlowToolDefinitionProperties originalFlow && !unchanged)
            {
                if (!await RefreshContractAsync() || !Current(request, key)) return;
                var flow = Selected()!;
                properties = originalFlow with
                {
                    InputSchema = original.Definition.FixedArguments is not null || original.Definition.InputSchemaBinding is not null ? original.Definition.InputSchema : InputSchemaContent!.Value,
                    OutputSchema = original.Definition.OutputSchemaBinding is not null ? original.Definition.OutputSchema : OutputSchemaContent,
                    Flow = new() { Name = flow.Id, Namespace = flow.Namespace, ScopeRef = flow.ScopeRef, UseActiveVersion = model.UseActiveVersion, Version = model.UseActiveVersion ? null : model.FlowVersion }
                };
            }
            properties = properties with { DisplayName = model.DisplayName, Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description, Enabled = original.Definition.Enabled, RequiresApproval = model.RequiresApproval, InvocationTimeoutSeconds = model.TimeoutSeconds!.Value, Version = model.PublicationVersion, Publish = model.Publish };
            var saved = await Client.UpdateAsync(original.Name, original.Namespace, new(properties), snapshot.ETag, cancellation.Token);
            if (!Current(request, key)) return;
            if (!Matches(saved.Value, original.Name, original.Namespace, original.ScopeRef, original.Uid)) { ClearResource(); loadState = new(ResourceLoadStatus.NotFound); return; }
            ApplySnapshot(saved); historyLoaded = false;
            if (acquisitionDefinition is null && Selected() is not null) await RefreshContractAsync();
            if (Current(request, key)) { savedFlowSelection = FlowSelection; savedFingerprint = Fingerprint; }
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) error = exception.Message; }
        finally { if (Current(request, key)) saving = false; }
    }
    private async Task SetEnabledAsync(bool value)
    {
        if (!CanMutate || saving || loading || resource is null || snapshot is null) return;
        var original = resource; var request = loadVersion; var key = loadedKey; saving = true; error = null;
        try
        {
            var incoming = await Client.SetEnabledAsync(original.Name, original.Namespace, value, snapshot.ETag, cancellation.Token);
            if (!Current(request, key)) return;
            if (!Matches(incoming.Value, original.Name, original.Namespace, original.ScopeRef, original.Uid)) { ClearResource(); loadState = new(ResourceLoadStatus.NotFound); return; }
            // Acknowledging activation rebases only its value and legitimate ETag, not the retained definition model/EditContext.
            snapshot = incoming; resource = incoming.Value; newerSnapshot = null; model.Enabled = incoming.Value.Definition.Enabled;
            var baseline = JsonSerializer.Deserialize<ToolDefinitionEditorModel>(savedFingerprint)!; baseline.Enabled = incoming.Value.Definition.Enabled;
            savedFingerprint = JsonSerializer.Serialize(baseline);
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) error = exception.Message; }
        finally { if (Current(request, key)) saving = false; }
    }
    private async Task DiscardAsync()
    {
        if (saving || loading || snapshot is null) return;
        var request = loadVersion; var key = loadedKey; saving = true;
        try { ApplySnapshot(newerSnapshot ?? snapshot); error = null; if (acquisitionDefinition is null && CanMutate && Selected() is not null) await RefreshContractAsync(); if (Current(request, key)) { savedFlowSelection = FlowSelection; savedFingerprint = Fingerprint; } }
        finally { if (Current(request, key)) saving = false; }
    }
    private async Task DeleteAsync()
    {
        if (!CanMutate || saving || loading || resource is null || snapshot is null) return;
        confirmDelete = false; saving = true; var request = loadVersion; var key = loadedKey; var target = resource;
        try { await Client.DeleteAsync(target.Name, target.Namespace, snapshot.ETag, cancellation.Token); if (!Current(request, key)) return; savedFingerprint = Fingerprint; invalidInput = false; Navigation.NavigateTo(BackUrl); }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) error = exception.Message; }
        finally { if (Current(request, key)) saving = false; }
    }
    private async Task LoadHistoryAsync()
    {
        if (historyLoading || resource?.ScopeRef is not { } owner) return;
        var request = loadVersion; var key = loadedKey; var target = resource; historyLoading = true; historyError = null;
        try
        {
            var publications = Client.GetVersionsAsync(owner, target.Name, target.Namespace, cancellation.Token); var consumers = Client.GetUsagesAsync(owner, target.Name, target.Namespace, cancellation.Token);
            await Task.WhenAll(publications, consumers); if (!Current(request, key)) return;
            versions = await publications; usages = await consumers; historyLoaded = true;
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(request, key)) historyError = exception.Message; }
        finally { if (Current(request, key)) historyLoading = false; }
    }
    private static ResourceLoadState LoadFailure(Exception exception) => new(exception is AgentstrationApiException api ? api.StatusCode switch
    {
        System.Net.HttpStatusCode.NotFound => ResourceLoadStatus.NotFound,
        System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden,
        _ => ResourceLoadStatus.Failed
    } : ResourceLoadStatus.Failed) { ErrorId = (exception as AgentstrationApiException)?.ErrorId };
    private FlowSummary? Selected() => flows.SingleOrDefault(value => FlowKey(value) == model.SelectedFlow);
    private static string FlowKey(FlowSummary flow) => flow.ScopeRef is { } owner ? $"{owner.Value}|{flow.Namespace.Value}|{flow.Id}" : "";
    private string ExistingFlowKey(ToolDefinitionFlowTarget target, ResourceNamespace ownerNamespace)
    {
        var ns = target.Namespace ?? ownerNamespace;
        if (target.ScopeRef is { } owner) return $"{owner.Value}|{ns.Value}|{target.Name}";
        var candidates = flows.Where(value => value.Namespace == ns && value.Id == target.Name).ToArray(); return candidates.Length == 1 ? FlowKey(candidates[0]) : $"relative|{ns.Value}|{target.Name}";
    }
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, IndentedJson);
    private JsonElement? InputSchemaContent => ParseOrNull(inputSchema);
    private JsonElement? OutputSchemaContent => ParseOrNull(outputSchema);
    private static JsonElement? ParseOrNull(string value) { if (string.IsNullOrWhiteSpace(value)) return null; try { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); } catch (JsonException) { return null; } }
    private static string FlowText(ToolDefinitionFlowTarget flow, ResourceNamespace ns) => $"{(flow.ScopeRef is { } owner ? owner.Value + " · " : "")}{flow.Namespace ?? ns}/{flow.Name}{(!flow.UseActiveVersion && !string.IsNullOrWhiteSpace(flow.Version) ? " · " + flow.Version : "")}";
    private string UsageUrl(ToolDefinitionUsage usage) => ResourceOwnershipContext.Resource(Services, usage.Kind switch { "Agent" => "/agents", "Flow" => "/flows", _ => "/tools/sets" }, usage.Name, usage.Namespace, usage.ScopeRef);
    private sealed record ConfigurationEntry(string Label, string Value);
    private IReadOnlyList<ConfigurationEntry> SavedConfiguration
    {
        get
        {
            if (resource is null) return [];
            var value = resource.Definition; var result = new List<ConfigurationEntry> { new(T("Enabled"), T(value.Enabled ? "Yes" : "No")), new(T("RequiresApproval"), T(value.RequiresApproval ? "Yes" : "No")), new(T("TimeoutSeconds"), value.InvocationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)), new(T("RelayVersion"), value.Version), new(T("PublishVersion"), T(value.Publish ? "Yes" : "No")), new(T("InputContract"), Pretty(value.InputSchema)), new(T("OutputContract"), value.OutputSchema is { } output ? Pretty(output) : "—") };
            if (value.Bindings.Count > 0 || value.FixedArguments is not null) result.Add(new(T("FixedInputBindings"), JsonSerializer.Serialize(new { value.Bindings, value.FixedArguments }, IndentedJson)));
            if (value.InputSchemaBinding is not null || value.OutputSchemaBinding is not null) result.Add(new(T("SchemaReferences"), JsonSerializer.Serialize(new { value.InputSchemaBinding, value.OutputSchemaBinding }, IndentedJson)));
            if (value is FlowToolDefinitionProperties flow) { result.Add(new(T("Flow"), FlowText(flow.Flow, resource.Namespace))); result.Add(new(T("UseActiveVersion"), T(flow.Flow.UseActiveVersion ? "Yes" : "No"))); }
            if (value is AcquisitionToolDefinitionProperties acquisition) { result.Add(new(T("AcquisitionTemplate"), $"{acquisition.Acquisition.TemplateNamespace}/{acquisition.Acquisition.TemplateId}")); result.Add(new(T("AcquisitionOutput"), acquisition.Acquisition.ArtifactName ?? "artifact")); }
            return result;
        }
    }
    public void Dispose() { disposed = true; if (subscribed && contextState is not null) contextState.Changed -= ContextChanged; loadVersion++; contractVersion++; formContext.OnFieldChanged -= FieldChanged; cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
