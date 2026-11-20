using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.ResourceManagement;
using Agentstration.Secrets.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Console;
using Agentstration.Web.Components.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class ToolRelayEditor
{
    private readonly ResourceSectionState sectionState = new();
    private readonly HashSet<object> creators = [];
    private bool creatorDirty => creators.Count > 0;
    private ResourceSnapshot<ToolDefinitionResource>? newerSnapshot;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private ResourceDefinitionForm? definitionForm;
    private ValidationMessageStore? relayMessages;
    private ConsoleContextState? contextState;
    private Guid? contextUser;
    private bool subscribed, initialSnapshotUsed;
    private bool HasPermission(string permission) => contextState is null || contextState.Current is not null && contextState.HasPermission(permission);
    private ResourceReadOnlyReason? DefinitionReadOnlyReason => !HasPermission("resources/write") ? ResourceReadOnlyReason.InsufficientPermissions : null;
    private bool refreshing, historyLoaded, disposed;
    private string? historyError, contextKey;
    private long operationVersion;
    private string SavedYaml => snapshot is null ? string.Empty : ResourceManifestSerializer.ToYaml(snapshot.Value);
    private string PersistentConfigFingerprint => JsonSerializer.Serialize(new { selected = SelectedKey, model.RequiresApproval, model.Timeout, rows = rows.Select(r => new { r.Path, r.Kind, r.Resource, r.Literal }) });
    private IReadOnlyList<ResourceNavigationItem> RelayDefinitionSections => [new("definition/bindings", T("FixedInputBindings"), "adjustments"), new("definition/contracts", T("Contracts"), "file-code")];
    private IReadOnlyList<ResourceNavigationItem> RelayAdditionalSections => [new("versions", T("RelayVersions"), "history"), new("usages", T("RelayUsages"), "list-check")];
    private IReadOnlyDictionary<string, ResourceSectionPresentation> RelayPresentations => new Dictionary<string, ResourceSectionPresentation>(StringComparer.Ordinal)
    {
        ["definition/identity"] = new() { ReadOnly = !CanMutate, HasRequiredFields = true, Fields = [nameof(model.Name), nameof(model.Namespace), nameof(model.DisplayName), nameof(model.Description), nameof(model.Tags)] },
        ["definition/configuration"] = new() { ReadOnly = !CanMutate, HasRequiredFields = true, Fields = [nameof(model.Version), nameof(model.RequiresApproval), nameof(model.Timeout), nameof(model.SourceKey), nameof(model.Enabled)] },
        ["definition/bindings"] = new() { ReadOnly = !CanMutate, Fields = [nameof(model.Bindings), nameof(model.InputEnvelope)] },
        ["definition/contracts"] = new() { ReadOnly = !CanMutate },
        ["yaml"] = new() { ReadOnly = true, ReadOnlyMessage = T("DefinitionYamlHelp") }
    };
    private IReadOnlyDictionary<string, string> RelayFieldLabels => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(model.DisplayName)] = T("DisplayName"), [nameof(model.Version)] = T("RelayVersion"), [nameof(model.Timeout)] = T("TimeoutSeconds")
    };
    private void SelectSection(string? value) { }
    private void NavigateSection(string path) => Navigation.NavigateTo(Navigation.Uri.Split('#')[0] + "#" + path);
    private bool Current(long version) => !disposed && version == operationVersion && contextKey == NavigationContext.ScopeRef && contextUser == contextState?.Current?.UserId && (contextState?.Current is null || contextState.HasPermission("resources/read"));
    private void ContextChanged() => _ = InvokeAsync(async () =>
    {
        if (disposed) return;
        if (contextKey != NavigationContext.ScopeRef || contextUser != contextState?.Current?.UserId) { ++operationVersion; ClearPrivate(); await InitializeAsync(); }
        else if (!HasPermission("resources/read")) { ++operationVersion; ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); }
        StateHasChanged();
    });
    private void CreatorChanged(BindingRow row, bool dirty) { if (dirty) creators.Add(row.DialogSectionId); else creators.Remove(row.DialogSectionId); }
    private void BridgeBindingValidation()
    {
        if (relayMessages is null) return;
        relayMessages.Clear(savedEditContext.Field(nameof(model.Bindings)));
        if (HasInvalidBindings) relayMessages.Add(savedEditContext.Field(nameof(model.Bindings)), T("RelayBindingInvalid"));
        savedEditContext.NotifyValidationStateChanged();
    }
    private async Task InvalidSubmitAsync(EditContext context) { if (HasInvalidBindings && definitionForm is not null) { NavigateSection("definition/bindings"); } await Task.CompletedTask; }
    private static bool SameResource(ToolDefinitionResource value, ToolDefinitionResource expected) => value.Kind == ToolResourceKinds.ToolDefinition && value.Definition is RelayToolDefinitionProperties && value.Uid == expected.Uid && value.Name == expected.Name && value.Namespace == expected.Namespace && value.ScopeRef == expected.ScopeRef;
    private static ResourceLoadState Failure(Exception exception) => new(exception is AgentstrationApiException api ? api.StatusCode switch
    {
        System.Net.HttpStatusCode.NotFound => ResourceLoadStatus.NotFound,
        System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden,
        _ => ResourceLoadStatus.Failed
    } : ResourceLoadStatus.Failed) { ErrorId = (exception as AgentstrationApiException)?.ErrorId };
    private void ClearPrivate()
    {
        snapshot = newerSnapshot = null; selected = null; baseline = null; rows.Clear(); tools = []; parameters = []; secrets = []; versions = []; usages = [];
        preview = ""; error = historyError = validationError = null; reviewing = sourceUpdateRequested = invalidInput = editorValidationInvalid = busy = refreshing = loading = false;
        model.Name = model.Namespace = model.DisplayName = model.Version = model.Tags = model.SourceKey = ""; model.Description = model.InputEnvelope = null; model.Bindings = []; creators.Clear();
        savedFingerprint = ""; editorKey++; historyLoaded = false;
    }
    private async Task InitializeAsync()
    {
        var version = ++operationVersion; contextKey = NavigationContext.ScopeRef; contextUser = contextState?.Current?.UserId; loading = true; loadState = new(ResourceLoadStatus.Loading);
        try
        {
            if (contextState is not null)
            {
                await contextState.EnsureLoadedAsync(cancellation.Token); if (disposed || version != operationVersion) return;
                if (contextState.Current is null) throw new InvalidOperationException(contextState.Error);
                contextUser = contextState.Current.UserId;
                if (!subscribed) { contextState.Changed += ContextChanged; subscribed = true; }
                if (!HasPermission("resources/read")) { ClearPrivate(); loadState = new(ResourceLoadStatus.Forbidden); return; }
            }
            if (CreationIdentity is null && !string.IsNullOrWhiteSpace(Name))
            {
                var supplied = initialSnapshotUsed ? null : InitialSnapshot; initialSnapshotUsed = true;
                var loaded = supplied ?? (string.IsNullOrWhiteSpace(ScopeRefValue)
                    ? await Definitions.GetAsync(Name, ResourceNamespace.Parse(NamespaceQuery), cancellation.Token)
                    : await Definitions.GetAsync(ResourceScopeRef.Parse(ScopeRefValue), Name, ResourceNamespace.Parse(NamespaceQuery), cancellation.Token));
                if (!Current(version)) return;
                if (loaded.Value.Name != Name || loaded.Value.Namespace != ResourceNamespace.Parse(NamespaceQuery) || loaded.Value.Definition is not RelayToolDefinitionProperties
                    || !string.IsNullOrWhiteSpace(ScopeRefValue) && loaded.Value.ScopeRef != ResourceScopeRef.Parse(ScopeRefValue))
                { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
                snapshot = loaded;
            }
            var catalogue = CreationIdentity?.ScopeRef is { } scope ? await Tools.GetToolsAsync(scope, cancellationToken: cancellation.Token) : await Tools.GetToolsAsync(cancellationToken: cancellation.Token);
            if (!Current(version)) return;
            tools = catalogue.Where(tool => tool.Definition.Enabled && tool.Definition.Discovery?.Available == true).ToArray();
            if (snapshot is not null)
            {
                await ReadHistoryAsync(version);
                if (!Current(version)) return;
                RestoreSavedFields();
            }
            else if (Source is not null)
            {
                var candidates = tools.Where(tool => ToolResourceIdentity.CatalogId(tool.Namespace, tool.Name) == Source && (SourceScopeRef is null || tool.ScopeRef == SourceScopeRef)).ToArray();
                selected = candidates.Length == 1 ? candidates[0] : null; BuildRows([]);
            }
            if (CanMutate && OwnerScope is { } owner)
            {
                var availableParameters = await Parameters.GetParametersAsync(owner, cancellation.Token);
                if (!Current(version)) return;
                parameters = availableParameters;
                var availableSecrets = await Secrets.GetSecretsInContextAsync(owner, cancellation.Token);
                if (!Current(version)) return;
                secrets = availableSecrets;
            }
            if (selected?.Definition.RequiresApproval == true) model.RequiresApproval = true;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = snapshot is not null };
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!Current(version)) return;
            var failure = Failure(exception);
            if (failure.Status is ResourceLoadStatus.NotFound or ResourceLoadStatus.Forbidden) ClearPrivate();
            if (CreationIdentity is not null) error = exception.Message;
            loadState = failure with { HasContent = snapshot is not null };
        }
        finally
        {
            if (Current(version))
            {
                loading = false; Preview(); creationBaseline = CreationFingerprint; savedEditContext = new(model); relayMessages = new(savedEditContext); savedFingerprint = Fingerprint;
            }
        }
    }
    private async Task ReadHistoryAsync(long version)
    {
        if (snapshot?.Value.ScopeRef is not { } owner) return;
        try
        {
            var publications = await Definitions.GetVersionsAsync(owner, snapshot.Value.Name, snapshot.Value.Namespace, cancellation.Token);
            if (!Current(version)) return;
            var consumers = await Definitions.GetUsagesAsync(owner, snapshot.Value.Name, snapshot.Value.Namespace, cancellation.Token);
            if (!Current(version)) return;
            versions = publications; usages = consumers; baseline = versions.FirstOrDefault(); historyLoaded = true; historyError = null;
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version)) { historyLoaded = false; historyError = exception.Message; } }
    }
    private async Task ReloadHistoryAsync() { if (busy || refreshing || loading || disposed) return; refreshing = true; try { await ReadHistoryAsync(operationVersion); } finally { if (!disposed) refreshing = false; } }
    private async Task RefreshAsync()
    {
        if (disposed || busy || refreshing || loading) return;
        if (snapshot is null) { await InitializeAsync(); return; }
        var target = snapshot.Value; var version = ++operationVersion; refreshing = true; loadState = new(ResourceLoadStatus.Refreshing) { HasContent = true };
        try
        {
            var saved = target.ScopeRef is { } owner ? await Definitions.GetAsync(owner, target.Name, target.Namespace, cancellation.Token) : await Definitions.GetAsync(target.Name, target.Namespace, cancellation.Token);
            if (!Current(version)) return;
            if (!SameResource(saved.Value, target)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            if (IsDirty || creatorDirty)
            {
                newerSnapshot = snapshot.ETag != saved.ETag || JsonSerializer.Serialize(snapshot.Value) != JsonSerializer.Serialize(saved.Value) ? saved : null;
            }
            else { snapshot = saved; Discard(); }
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!Current(version)) return;
            var failure = Failure(exception); if (failure.Status is ResourceLoadStatus.NotFound or ResourceLoadStatus.Forbidden) ClearPrivate();
            loadState = failure with { HasContent = snapshot is not null };
        }
        finally { if (Current(version)) refreshing = false; }
    }
    private async Task SetEnabledAsync(bool value)
    {
        if (disposed || !CanMutate || busy || refreshing || snapshot is null) return;
        var target = snapshot.Value; var version = operationVersion; busy = true; error = null;
        try
        {
            var saved = await Definitions.SetEnabledAsync(target.Name, target.Namespace, value, snapshot.ETag, cancellation.Token);
            if (!Current(version)) return;
            if (!SameResource(saved.Value, target)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            snapshot = saved; newerSnapshot = null; model.Enabled = saved.Value.Definition.Enabled;
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version)) error = exception.Message; }
        finally { if (Current(version)) busy = false; }
    }
    private async Task SaveDefinitionAsync()
    {
        if (disposed || busy || refreshing || !CanMutate) return;
        if (CreationIdentity is { } identity)
        {
            if (!identity.IsComplete) { error = T("CreationIdentityInvalid"); return; }
            model.Name = identity.Name; model.DisplayName = identity.DisplayName; model.Namespace = identity.Namespace; model.Version = identity.Version; model.Description = identity.Description;
        }
        var bindingsValid = bindingEditContext.Validate();
        if (selected is null || !bindingsValid || HasInvalidBindings || invalidInput || CreationIdentity is null && !savedEditContext.Validate())
        {
            if (selected is null && relayMessages is not null) { relayMessages.Add(savedEditContext.Field(nameof(model.SourceKey)), T("SelectSourceTool")); savedEditContext.NotifyValidationStateChanged(); }
            BridgeBindingValidation(); if (definitionForm is not null) await definitionForm.RevealValidationAsync(); return;
        }
        busy = true; error = null; var version = operationVersion; var original = snapshot?.Value;
        try
        {
            var source = new ToolRelayTarget { Name = selected.Name, Namespace = selected.Namespace, ScopeRef = selected.ScopeRef!.Value, Uid = selected.Uid, Generation = selected.Generation };
            var properties = (draftBase ?? new RelayToolDefinitionProperties { DisplayName = model.DisplayName, Target = source }) with
            {
                DisplayName = model.DisplayName, Description = model.Description, Version = model.Version, Enabled = snapshot?.Value.Definition.Enabled ?? model.Enabled,
                RequiresApproval = model.RequiresApproval, InvocationTimeoutSeconds = model.Timeout, Bindings = BuildBindings(), InputEnvelope = model.InputEnvelope, Target = source
            };
            var saved = snapshot is null ? await Definitions.CreateAsync(new(model.Name, properties, model.Namespace, OwnerScope), cancellation.Token)
                : await Definitions.UpdateAsync(snapshot.Value.Name, snapshot.Value.Namespace, new(properties), snapshot.ETag, cancellation.Token);
            if (!Current(version)) return;
            if (original is not null && !SameResource(saved.Value, original)) { ClearPrivate(); loadState = new(ResourceLoadStatus.NotFound); return; }
            snapshot = saved; newerSnapshot = null; savedFingerprint = Fingerprint; invalidInput = false; draftBase = (RelayToolDefinitionProperties)saved.Value.Definition;
            if (CreationIdentity is not null) await OnCreated.InvokeAsync(saved.Value);
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version)) error = exception.Message; }
        finally { if (Current(version)) busy = false; }
    }
    private async Task PublishDefinitionAsync()
    {
        if (disposed || busy || refreshing || !CanMutate) return;
        await SaveAsync();
        if (error is not null || snapshot is null || IsDirty || HasInvalidBindings || !bindingEditContext.Validate()) return;
        busy = true; var version = operationVersion; var target = snapshot.Value;
        try
        {
            _ = await Definitions.PublishAsync(target.Name, target.Namespace, model.Version, cancellation.Token, snapshot.ETag);
            if (!Current(version)) return;
            await ReadHistoryAsync(version); if (!Current(version)) return;
            reviewing = false; NavigateSection("versions");
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
        catch (Exception exception) { if (Current(version)) error = exception.Message; }
        finally { if (Current(version)) busy = false; }
    }
}
