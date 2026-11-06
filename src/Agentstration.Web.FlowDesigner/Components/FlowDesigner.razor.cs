using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static Agentstration.Web.FlowDesigner.Components.FlowInspectorPresentation;

namespace Agentstration.Web.FlowDesigner.Components;

// Owns the editor store, catalog loading, selection and draft lifecycle.
// Presentation components receive snapshots and delegate all mutations back to this instance.
public partial class FlowDesigner
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? autosave;
    private FlowCanvas? canvas;
    private IJSObjectReference? workspaceModule;
    private IJSObjectReference? keyboardRegistration;
    private DotNetObjectReference<FlowDesigner>? selfReference;
    private IReadOnlyList<FlowDesignerAgent> agents = [];
    private IReadOnlyList<FlowDesignerAgent> routerAgents = [];
    private IReadOnlyList<FlowDesignerFlow> flows = [];
    private IReadOnlyList<FlowDesignerFlowVersion> flowVersions = [];
    private IReadOnlyList<FlowDesignerTool> tools = [];
    private IReadOnlyList<FlowDesignerSchemaVersion> schemaVersions = [];
    private string? loadedResource;
    private string transitionFrom = string.Empty;
    private string transitionTo = string.Empty;
    private string transitionEvent = "completed";
    private string publishVersion = "1.0.0";
    private PublicationOutcome? publication;
    private bool showAllErrorTransitions;
    private JsonElement runInput = JsonSerializer.SerializeToElement(new { prompt = "How can I optimize this SQL query?" });
    private string? error;
    private string? runError;
    private bool saving;
    private bool publishing;
    private bool showRun;
    private ConnectionContractPrompt? connectionContractPrompt;
    private bool runningDraft;
    private bool runInputValid = true;
    private bool loadingAgents;
    private bool focusMode;
    private bool validationExpanded;
    private bool moreActionsOpen;
    private bool? paletteVisibility;
    private bool? inspectorVisibility;
    private bool showStepSettings;
    private FlowStepSettingsTab activeStepSettingsTab = FlowStepSettingsTab.General;
    private long layoutRevision;
    private long flowCallRenderRevision;
    private int toolPickerRevision;
    private string toolSearch = string.Empty;

    private FlowTransitionDefinition? SelectedTransition => Editor.State.Resource?.Definition.Transitions
        .FirstOrDefault(transition => transition.Id == Editor.State.Selection.TransitionId);

    [Parameter, EditorRequired] public string ResourceId { get; set; } = string.Empty;
    [Parameter] public bool IsReadOnly { get; set; }
    [Parameter] public ResourceNamespace Namespace { get; set; } = ResourceNamespace.Default;

    private string WorkspaceClasses => string.Join(' ', new[]
    {
        Editor.State.Mode.ToString().ToLowerInvariant(),
        paletteVisibility is true ? "palette-open" : paletteVisibility is false ? "palette-closed" : null,
        inspectorVisibility is true ? "inspector-open" : inspectorVisibility is false ? "inspector-closed" : null,
        focusMode ? "focus-mode" : null
    }.Where(value => value is not null));

    private FlowStepDefinition? SelectedStep => Editor.State.Resource?.Definition.Steps.FirstOrDefault(step => step.Name == Editor.State.Selection.StepName);
    private FlowDesignerAgent? SelectedAgent => SelectedStep is AgentFlowStepDefinition step
        ? ResolveAgent(step.Agent)
        : null;
    private FlowDesignerFlow? SelectedFlow => SelectedStep is FlowCallStepDefinition step
        ? ResolveFlow(step.Flow)
        : null;
    private FlowDesignerFlowVersion? SelectedFlowVersion => SelectedStep is FlowCallStepDefinition call ? FlowVersion(call) : null;
    private FlowDesignerFlowVersion? FlowVersion(FlowCallStepDefinition call)
        => flowVersions.FirstOrDefault(version => version.Version == (call.Flow.VersionStrategy == FlowCallVersionStrategy.Exact
            ? call.Flow.Version
            : flows.FirstOrDefault(flow => ResourceKey(flow.ScopeRef, flow.Namespace, flow.Name) == FlowKey(call.Flow))?.ActiveVersion));
    private FlowDesignerTool? SelectedTool => SelectedStep is ToolFlowStepDefinition tool
        ? ResolveTool(tool.Tool)
        : null;
    private InspectorResourceSummary? SelectedInspectorResource => SelectedStep switch
    {
        AgentFlowStepDefinition when SelectedAgent is { } agent => new(agent.DisplayName, agent.Name, AgentUrl(agent), "OpenAgentInNewTab"),
        FlowCallStepDefinition when SelectedFlow is { } flow => new(flow.DisplayName, flow.Name, FlowUrl(flow), "OpenFlowInNewTab"),
        ToolFlowStepDefinition when SelectedTool is { } tool => new(tool.DisplayName, tool.Name, ToolUrl(tool), "OpenToolInNewTab"),
        _ => null
    };
    private InputFlowStepDefinition? ParentInputStep => Editor.State.Resource?.Definition.Steps.OfType<InputFlowStepDefinition>().SingleOrDefault();
    private bool ParentInputHasExplicitContract => ParentInputStep is { Schema: not null } or { SchemaBinding: not null }
        || Editor.State.Resource?.Definition is { InputSchema: not null } or { InputSchemaBinding: not null };
    private JsonElement? EffectiveInputSchema
    {
        get
        {
            if (Editor.State.Resource is not { } resource) return null;
            var input = resource.Definition.Steps.OfType<InputFlowStepDefinition>().FirstOrDefault();
            return ResolveSchema(input?.SchemaBinding, input?.Schema)
                ?? ResolveSchema(resource.Definition.InputSchemaBinding, resource.Definition.InputSchema);
        }
    }
    private JsonElement? EffectiveIncomingSchema
    {
        get
        {
            if (Editor.State.Resource is not { } resource || SelectedStep is null) return null;
            var candidates = resource.Definition.Transitions
                .Where(transition => transition.ToStep == SelectedStep.Name)
                .Select(ResolveTransitionOutputSchema)
                .Where(schema => schema is not null)
                .Select(schema => schema!.Value)
                .ToArray();
            var distinct = new List<JsonElement>();
            foreach (var candidate in candidates)
                if (!distinct.Any(existing => JsonElement.DeepEquals(existing, candidate))) distinct.Add(candidate);
            return distinct.Count == 1 ? distinct[0] : null;
        }
    }
    private IEnumerable<FlowDesignerTool> VisibleTools => string.IsNullOrWhiteSpace(toolSearch)
        ? tools
        : tools.Where(item => item.DisplayName.Contains(toolSearch, StringComparison.CurrentCultureIgnoreCase)
            || item.Name.Contains(toolSearch, StringComparison.OrdinalIgnoreCase)
            || item.Namespace.Value.Contains(toolSearch, StringComparison.OrdinalIgnoreCase));
    private string SaveText => T($"SaveState.{Editor.State.SaveState}");
    private string T(string key, params object[] arguments) => Localizer[key, arguments].Value;
    [Parameter] public ResourceScopeRef? ScopeRef { get; set; }
    private FlowDesignerTarget Target => new(Namespace, ResourceId, ScopeRef);
    private ResourceScopeRef OwnerScope => Editor.State.Resource?.ScopeRef ?? ScopeRef
        ?? throw new InvalidOperationException("The Flow owner scope is unavailable.");
    private bool ReadOnly => loadingTarget || IsReadOnly || Editor.State.IsReadOnly;

    protected override void OnInitialized() => Editor.StateChanged += EditorChanged;

    protected override async Task OnParametersSetAsync()
    {
        var targetKey = $"{ScopeRef?.Value}/{Namespace.Value}/{ResourceId}";
        if (loadedResource == targetKey) return;
        loadedResource = targetKey;
        targetCancellation?.Cancel();
        targetCancellation?.Dispose();
        autosave?.Cancel();
        targetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = targetCancellation.Token;
        var target = Target;
        loadingTarget = true;
        saving = publishing = runningDraft = false;
        publication = null;
        try
        {
            error = null;
            var loaded = await Backend.LoadAsync(target, token);
            token.ThrowIfCancellationRequested();
            Editor.Load(loaded, target);
            var owner = OwnerScope;
            var loadedAgents = await Resources.GetAgentsAsync(owner, token);
            var loadedFlows = await Resources.GetFlowsAsync(owner, token);
            var loadedTools = await Resources.GetToolsAsync(owner, token);
            var loadedSchemas = await Resources.GetSchemaVersionsAsync(token);
            var versions = loaded.IsReadOnly ? [] : await Resources.GetFlowVersionsAsync(owner, target.Namespace, target.ResourceId, token);
            token.ThrowIfCancellationRequested();
            agents = routerAgents = loadedAgents;
            flows = loadedFlows;
            tools = loadedTools;
            schemaVersions = loadedSchemas;
            flowVersions = [];
            loadingTarget = false;
            await RefreshFlowCallOutputsAsync(loaded.Resource.Definition);
            token.ThrowIfCancellationRequested();
            if (!ReadOnly) publishVersion = SuggestedPublishVersion(versions);
            transitionFrom = loaded.Resource.Definition.EntryStep;
            transitionTo = loaded.Resource.Definition.Steps.Skip(1).FirstOrDefault()?.Name ?? transitionFrom;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) error = exception.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested) loadingTarget = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        workspaceModule = await JS.InvokeAsync<IJSObjectReference>(
            "import", "./_content/Agentstration.Web.FlowDesigner/js/flow-designer-workspace.js?v=20261013-1");
        selfReference = DotNetObjectReference.Create(this);
        keyboardRegistration = await workspaceModule.InvokeAsync<IJSObjectReference>("registerKeyboardShortcuts", selfReference);
    }

    private async Task DispatchAsync(IFlowEditorCommand command)
    {
        if (ReadOnly) return;
        await Editor.DispatchAsync(command, cancellation.Token);
        ScheduleAutosave();
    }

    private Task MoveStepAsync(MoveStepCommand command) => DispatchAsync(command);
    private async Task CreateTransitionAsync(FlowTransitionDefinition transition)
    {
        await DispatchAsync(new AddTransitionCommand(transition));
        Editor.SelectTransition(transition.Id);
        await OfferConnectedInputContractAsync(transition);
    }
    private async Task ReconnectTransitionAsync(ReconnectTransitionCommand command)
    {
        await DispatchAsync(command);
        if (Editor.State.Resource?.Definition.Transitions.FirstOrDefault(transition => transition.Id == command.Id) is { } transition)
            await OfferConnectedInputContractAsync(transition);
    }
    private async Task RemoveTransitionAsync(string id)
    {
        await DispatchAsync(new RemoveTransitionCommand(id));
        if (Editor.State.Selection.TransitionId == id) Editor.SelectTransition(null);
    }
    private Task SelectTransitionAsync(string? id)
    {
        Editor.SelectTransition(id);
        return Task.CompletedTask;
    }
    private async Task SelectStepAsync(string? name)
    {
        Editor.SelectStep(name);
        if (name is not null)
        {
            inspectorVisibility = true;
            paletteVisibility = null;
        }
        toolSearch = string.Empty;
        toolPickerRevision++;
        if (!ReadOnly && SelectedStep is AgentFlowStepDefinition agent
            && (string.IsNullOrWhiteSpace(agent.DisplayName) || agent.DisplayName == "Agent")
            && SelectedAgent is { } selectedAgent)
            await UpdateSelectedAsync(agent with { DisplayName = selectedAgent.DisplayName });
        if (SelectedStep is FlowCallStepDefinition call) await LoadFlowVersionsAsync(call);
    }
    private async Task OpenStepSettingsAsync(string name)
    {
        await SelectStepAsync(name);
        activeStepSettingsTab = FlowStepSettingsTab.General;
        showStepSettings = SelectedStep is not null;
    }
    private void OpenSelectedStepSettings()
    {
        activeStepSettingsTab = FlowStepSettingsTab.General;
        showStepSettings = SelectedStep is not null;
    }
    private void CloseStepSettings() => showStepSettings = false;
    private async Task LayoutAsync(bool vertical)
    {
        await DispatchAsync(new ApplyAutoLayoutCommand(vertical));
        layoutRevision++;
    }
    private void Center() => canvas?.Center();
    private void ToggleErrorTransitions() => showAllErrorTransitions = !showAllErrorTransitions;
    private void ToggleDesktopPalette() => paletteVisibility = paletteVisibility is false ? null : false;
    private void ToggleDesktopInspector() => inspectorVisibility = inspectorVisibility is false ? null : false;
    private void ToggleCompactPalette()
    {
        paletteVisibility = paletteVisibility is true ? null : true;
        if (paletteVisibility is true) inspectorVisibility = null;
    }
    private void ToggleCompactInspector()
    {
        inspectorVisibility = inspectorVisibility is true ? null : true;
        if (inspectorVisibility is true) paletteVisibility = null;
    }
    private void ClosePaletteDrawer() => paletteVisibility = null;
    private void CloseInspectorDrawer() => inspectorVisibility = null;
    private void ToggleFocusMode() => focusMode = !focusMode;

    [JSInvokable]
    public Task ExitFullScreenAsync()
    {
        if (showStepSettings)
        {
            showStepSettings = false;
            return InvokeAsync(StateHasChanged);
        }
        if (!focusMode || showRun) return Task.CompletedTask;
        focusMode = false;
        return InvokeAsync(StateHasChanged);
    }
    private void ToggleValidation() => validationExpanded = !validationExpanded;
    private void EditorChanged(object? sender, EventArgs args) => _ = InvokeAsync(StateHasChanged);
    private void SourceChanged(string source) { if (ReadOnly) return; Editor.SetSource(source, null); }

    private Task SaveAsync() => WithSavedDraftAsync();

    private Task ValidateAsync() => WithSavedDraftAsync(async (target, token) =>
    {
        var result = await Backend.ValidateAsync(target, token);
        token.ThrowIfCancellationRequested();
        Editor.SetIssues(result.Issues);
        validationExpanded = true;
    });

    private async Task PublishAsync()
    {
        if (ReadOnly || publishing) return;
        publishing = true;
        publication = null;
        var token = targetCancellation!.Token;
        var requestedVersion = publishVersion;
        try
        {
            await WithSavedDraftAsync(async (target, operationToken) =>
            {
                var version = await Backend.PublishAsync(target, new(requestedVersion), operationToken);
                operationToken.ThrowIfCancellationRequested();
                var owner = OwnerScope;
                _ = await Resources.GetFlowVersionsAsync(owner, target.Namespace, target.ResourceId, operationToken);
                var refreshedFlows = await Resources.GetFlowsAsync(owner, operationToken);
                operationToken.ThrowIfCancellationRequested();
                flows = refreshedFlows;
                publication = new(version.Version, Activated: true);
                publishVersion = NextVersion(version.Version);
            });
        }
        finally { if (!token.IsCancellationRequested) publishing = false; }
    }
    private void OpenRun() { if (ReadOnly) return; showRun = true; error = null; runError = null; runInputValid = true; runInput = ParseInput("{}"); }
    private Task UpdateRunInputValidation(JsonSchemaInputValidationState value) { runInputValid = value.IsValid; return Task.CompletedTask; }
    private async Task RunDraftAsync()
    {
        if (ReadOnly || !runInputValid || runningDraft) return;
        runningDraft = true;
        runError = null;
        var token = targetCancellation!.Token;
        var input = runInput.Clone();
        try
        {
            await WithSavedDraftAsync(async (target, operationToken) =>
            {
                try
                {
                    var run = await Backend.RunDraftAsync(target, new(input, Trigger: FlowRunTrigger.Manual), operationToken);
                    operationToken.ThrowIfCancellationRequested();
                    Navigation.NavigateTo($"/flow-runs/{run.Id}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!operationToken.IsCancellationRequested) runError = exception.Message;
                }
            });
        }
        finally { if (!token.IsCancellationRequested) runningDraft = false; }
    }
    private static JsonElement ParseInput(string value) { try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : JsonSerializer.SerializeToElement(new { }); } catch (JsonException) { return JsonSerializer.SerializeToElement(new { }); } }
    private static bool TryParseJson(string value, out JsonElement parsed)
    {
        try { using var document = JsonDocument.Parse(value); parsed = document.RootElement.Clone(); return true; }
        catch (JsonException) { parsed = default; return false; }
    }
    private Task ApplySourceAsync(string source)
    {
        if (ReadOnly) return Task.CompletedTask;
        Editor.SetSource(source, null);
        return WithSavedDraftAsync();
    }

    private void Undo() { if (ReadOnly) return; Editor.Undo(); ScheduleAutosave(); }
    private void Redo() { if (ReadOnly) return; Editor.Redo(); ScheduleAutosave(); }
    private void SelectIssue(FlowValidationIssue issue) { if (issue.StepId is not null) Editor.SelectStep(issue.StepId); }
    private void ScheduleAutosave() { if (ReadOnly) return; autosave?.Cancel(); autosave?.Dispose(); autosave = CancellationTokenSource.CreateLinkedTokenSource(targetCancellation!.Token); _ = AutosaveAsync(autosave.Token); }
    private async Task AutosaveAsync(CancellationToken token) { try { await Task.Delay(TimeSpan.FromSeconds(1.5), token); await WithSavedDraftAsync(includeSource: false, queuedCancellation: token); } catch (OperationCanceledException) { } }
    private string Unique(string prefix) { var name = prefix; var i = 1; while (Editor.State.Resource!.Definition.Steps.Any(step => step.Name == name)) name = $"{prefix}-{++i}"; return name; }
    private static string UniqueRoute(string prefix, IReadOnlyCollection<FlowRouterCandidate> candidates) { var route = prefix; var i = 1; while (candidates.Any(candidate => candidate.Route == route)) route = $"{prefix}-{++i}"; return route; }
    private static string RouteKey(string value) { var result = new string(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'); return string.IsNullOrWhiteSpace(result) ? "route" : result; }
    private FlowResourceReference AgentReference(FlowDesignerAgent agent) => new(agent.Name, Namespace: agent.Namespace == Namespace ? null : agent.Namespace, ScopeRef: agent.ScopeRef);
    private string AgentSelectionValue(FlowResourceReference reference) => reference.ResourceId.StartsWith("${", StringComparison.Ordinal)
        ? reference.ResourceId
        : ResourceKey(reference.ScopeRef ?? ResolveAgent(reference)?.ScopeRef, reference.Namespace ?? Namespace, reference.ResourceId);
    private FlowCallReference DefaultFlowReference()
    {
        var target = flows.FirstOrDefault(item => item.Name != ResourceId || item.Namespace != Namespace || item.ScopeRef != OwnerScope);
        return target is null ? new(UnconfiguredFlow) : new(target.Name, Namespace: target.Namespace == Namespace ? null : target.Namespace, ScopeRef: target.ScopeRef);
    }
    private FlowToolReference DefaultToolReference()
    {
        var target = tools.FirstOrDefault();
        return target is null ? new(UnconfiguredTool) : new(target.Name, target.Namespace == Namespace ? null : target.Namespace, target.ScopeRef);
    }
    private async Task LoadFlowVersionsAsync(FlowCallStepDefinition call)
    {
        var token = targetCancellation!.Token;
        var versions = await Resources.GetFlowVersionsAsync(ResolveFlowScope(call.Flow), call.Flow.Namespace ?? Namespace, call.Flow.ResourceId, token);
        token.ThrowIfCancellationRequested();
        flowVersions = versions;
        await SyncFlowCallOutputsAsync(call, versions);
    }
    private async Task RefreshFlowCallOutputsAsync(FlowGraphDefinition definition)
    {
        var token = targetCancellation!.Token;
        var resolved = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal);
        var versionsByTarget = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal);
        foreach (var call in definition.Steps.OfType<FlowCallStepDefinition>())
        {
            var targetNamespace = call.Flow.Namespace ?? Namespace;
            var targetScope = ResolveFlowScope(call.Flow);
            var key = ResourceKey(targetScope, targetNamespace, call.Flow.ResourceId);
            if (!versionsByTarget.TryGetValue(key, out var versions))
            {
                versions = await Resources.GetFlowVersionsAsync(targetScope, targetNamespace, call.Flow.ResourceId, token);
                token.ThrowIfCancellationRequested();
                versionsByTarget[key] = versions;
            }
            if (ResolveFlowVersion(call, versions) is { NamedOutputs.Count: > 0 } selected)
                resolved[call.Name] = selected.NamedOutputs;
        }
        token.ThrowIfCancellationRequested();
        if (Editor.SetFlowCallOutputs(resolved)) flowCallRenderRevision++;
        if (!ReadOnly)
        {
            var revision = Editor.State.LocalRevision;
            await Editor.DispatchAsync(new ReconcileFlowCallTransitionsCommand(resolved), cancellation.Token);
            if (Editor.State.LocalRevision != revision) ScheduleAutosave();
        }
    }
    private async Task SyncFlowCallOutputsAsync(FlowCallStepDefinition call, IReadOnlyList<FlowDesignerFlowVersion> versions)
    {
        var outputs = ResolveFlowVersion(call, versions)?.NamedOutputs ?? [];
        if (Editor.SetFlowCallOutputs(call.Name, outputs))
            flowCallRenderRevision++;
        if (ReadOnly || outputs.Count == 0) return;

        var revision = Editor.State.LocalRevision;
        await Editor.DispatchAsync(
            new ReconcileFlowCallTransitionsCommand(
                new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal)
                {
                    [call.Name] = outputs
                }),
            cancellation.Token);
        if (Editor.State.LocalRevision != revision) ScheduleAutosave();
    }
    private FlowDesignerFlowVersion? ResolveFlowVersion(FlowCallStepDefinition call, IReadOnlyList<FlowDesignerFlowVersion> versions)
    {
        var version = call.Flow.VersionStrategy == FlowCallVersionStrategy.Exact
            ? call.Flow.Version
            : flows.FirstOrDefault(flow => ResourceKey(flow.ScopeRef, flow.Namespace, flow.Name) == FlowKey(call.Flow))?.ActiveVersion;
        return version is null ? null : versions.FirstOrDefault(candidate => candidate.Version == version);
    }
    private IReadOnlyList<string> TransitionEvents(FlowTransitionDefinition transition)
    {
        return Editor.State.Diagram.Nodes.FirstOrDefault(node => node.Name == transition.FromStep)?.OutputEvents ?? [];
    }
    private Task<SchemaFormatValidationResult> ValidateSchemaDefinitionAsync(ValidateSchemaDefinitionRequest request) =>
        Resources.ValidateSchemaDefinitionAsync(request, cancellation.Token);
    private Task<SchemaFormatValidationResult> ValidateSchemaInstanceAsync(ValidateSchemaInstanceRequest request) =>
        Resources.ValidateSchemaInstanceAsync(request, cancellation.Token);
    private JsonElement? ResolveSchema(SchemaBinding? binding, JsonElement? legacy)
    {
        if (binding?.Inline is { } inline) return inline.Content;
        if (binding?.Reference is { } reference)
            return schemaVersions.FirstOrDefault(candidate => candidate.Name == reference.Name
                && candidate.Version == reference.Version
                && candidate.Namespace == (reference.Namespace ?? ResourceNamespace.Default)
                && (reference.ScopeRef is null || candidate.ScopeRef == reference.ScopeRef))?.Content;
        return legacy;
    }
    private JsonElement? ResolveStepOutputSchema(FlowStepDefinition? step)
    {
        if (Editor.State.Resource is not { } resource) return null;
        return step switch
        {
            InputFlowStepDefinition input => ResolveSchema(input.SchemaBinding, input.Schema)
                ?? ResolveSchema(resource.Definition.InputSchemaBinding, resource.Definition.InputSchema),
            TransformFlowStepDefinition transform => ResolveSchema(transform.SchemaBinding, transform.Schema),
            ToolFlowStepDefinition tool => tools.FirstOrDefault(candidate => ToolKey(candidate) == ToolKey(tool.Tool))?.OutputSchema,
            FlowCallStepDefinition call => FlowVersion(call)?.OutputSchema,
            _ => null
        };
    }
    private JsonElement? ResolveTransitionOutputSchema(FlowTransitionDefinition transition)
    {
        if (Editor.State.Resource is not { } resource) return null;
        var source = resource.Definition.Steps.FirstOrDefault(step => step.Name == transition.FromStep);
        if (source is FlowCallStepDefinition call && FlowVersion(call) is { } version)
            return version.NamedOutputs.FirstOrDefault(output => output.Name == transition.Event)?.Schema
                ?? version.OutputSchema;
        return ResolveStepOutputSchema(source);
    }
    private string IncomingContractUnavailableText()
    {
        if (Editor.State.Resource is not { } resource || SelectedStep is null) return T("InheritedContractUnavailable");
        var incoming = resource.Definition.Transitions.Where(transition => transition.ToStep == SelectedStep.Name).ToArray();
        if (incoming.Length == 0) return T("NoIncomingTransitionContract");
        if (incoming.Length != 1) return T("InheritedContractUnavailable");
        var source = resource.Definition.Steps.FirstOrDefault(step => step.Name == incoming[0].FromStep);
        return source is null
            ? T("InheritedContractUnavailable")
            : T("UpstreamContractUnavailable", source.DisplayName ?? source.Name);
    }
    private string FlowKey(FlowCallReference reference) => ResourceKey(reference.ScopeRef ?? ResolveFlow(reference)?.ScopeRef, reference.Namespace ?? Namespace, reference.ResourceId);
    private static bool TryParseFlowKey(string value, out (ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Name) target)
    {
        var parts = value.Split('|', 3);
        if (parts.Length == 3 && ResourceScopeRef.TryParse(parts[0], out var scopeRef) && !string.IsNullOrWhiteSpace(parts[2])) { target = (scopeRef, ResourceNamespace.Parse(parts[1]), parts[2]); return true; }
        target = default; return false;
    }
    private static string ToolKey(FlowDesignerTool tool) => ResourceKey(tool.ScopeRef, tool.Namespace, tool.Name);
    private string ToolKey(FlowToolReference reference) => ResourceKey(reference.ScopeRef ?? ResolveTool(reference)?.ScopeRef, reference.Namespace ?? Namespace, reference.ResourceId);
    private FlowDesignerAgent? ResolveAgent(FlowResourceReference reference) => reference.ScopeRef is { } exact
        ? agents.FirstOrDefault(agent => agent.ScopeRef == exact && agent.Namespace == (reference.Namespace ?? Namespace) && agent.Name == reference.ResourceId)
        : agents.Where(agent => agent.Namespace == (reference.Namespace ?? Namespace) && agent.Name == reference.ResourceId).SingleOrDefault();
    private FlowDesignerFlow? ResolveFlow(FlowCallReference reference) => reference.ScopeRef is { } exact
        ? flows.FirstOrDefault(flow => flow.ScopeRef == exact && flow.Namespace == (reference.Namespace ?? Namespace) && flow.Name == reference.ResourceId)
        : flows.Where(flow => flow.Namespace == (reference.Namespace ?? Namespace) && flow.Name == reference.ResourceId).SingleOrDefault();
    private FlowDesignerTool? ResolveTool(FlowToolReference reference) => reference.ScopeRef is { } exact
        ? tools.FirstOrDefault(tool => tool.ScopeRef == exact && tool.Namespace == (reference.Namespace ?? Namespace) && tool.Name == reference.ResourceId)
        : tools.Where(tool => tool.Namespace == (reference.Namespace ?? Namespace) && tool.Name == reference.ResourceId).SingleOrDefault();
    private ResourceScopeRef ResolveFlowScope(FlowCallReference reference) => reference.ScopeRef ?? ResolveFlow(reference)?.ScopeRef ?? OwnerScope;
    private bool FlowCallReceivesParentInput(FlowCallStepDefinition call) =>
        ParentInputStep is { } input
        && PassesIncomingTransitionOutput(call.InputMapping)
        && Editor.State.Resource?.Definition.Transitions.Any(transition =>
            transition.FromStep == input.Name && transition.ToStep == call.Name) == true;

    private sealed record ConnectionContractPrompt(
        FlowConnectionContractSource Source,
        string ResourceDisplayName,
        JsonElement Schema,
        SchemaVersionReference? Reference,
        bool ReplacesExistingContract);
    private static bool IsValidStepName(string value) => value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    private static string SuggestedPublishVersion(IReadOnlyList<FlowDesignerFlowVersion> versions)
    {
        Version? latest = null;
        foreach (var published in versions)
        {
            var parsed = ParseVersion(published.Version);
            if (parsed is not null && (latest is null || parsed.CompareTo(latest) > 0)) latest = parsed;
        }
        if (latest is null) return "1.0.0";
        var release = latest.ToString();
        return versions.Any(published => string.Equals(published.Version, release, StringComparison.Ordinal))
            ? NextVersion(release)
            : release;
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Split('-', 2)[0], out var parsed) && parsed.Build >= 0 && parsed.Revision < 0 ? parsed : null;

    private static string NextVersion(string value)
    {
        var parsed = ParseVersion(value);
        return parsed is null ? "1.0.0" : $"{parsed.Major}.{parsed.Minor}.{parsed.Build + 1}";
    }
    private sealed record PublicationOutcome(string Version, bool Activated);
    private const string UnconfiguredAgent = "unconfigured";
    private const string UnconfiguredFlow = "unconfigured";
    private const string UnconfiguredTool = "unconfigured";
    private sealed record InspectorResourceSummary(string DisplayName, string Name, string Url, string OpenLabelKey);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        Editor.StateChanged -= EditorChanged;
        autosave?.Cancel(); autosave?.Dispose(); targetCancellation?.Cancel(); targetCancellation?.Dispose(); cancellation.Cancel(); cancellation.Dispose();
        try
        {
            if (keyboardRegistration is not null)
                await keyboardRegistration.InvokeVoidAsync("dispose");
            if (keyboardRegistration is not null)
                await keyboardRegistration.DisposeAsync();
            if (workspaceModule is not null)
                await workspaceModule.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The browser circuit can close before the component is disposed.
        }
        selfReference?.Dispose();
    }
}
