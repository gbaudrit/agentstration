using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Pages;

public partial class SystemExplorer
{
    [SupplyParameterFromQuery(Name = "namespace")] public string? NamespaceQuery { get; set; }
    [SupplyParameterFromQuery(Name = "scopeRef")] public string? OwnerQuery { get; set; }
    [SupplyParameterFromQuery(Name = "root")] public string? RootQuery { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? ContextQuery { get; set; }
    [SupplyParameterFromQuery(Name = "renderer")] public string? RendererQuery { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private CancellationTokenSource? cancellation;
    private long revision;
    private bool loading = true, busy, showDiagram = true, diagramFailed, canWrite, canDelete, confirmDelete;
    private string? error, actionError;
    private string namespaceInput = ResourceNamespace.DefaultValue, newLabel = "", renameLabel = "";
    private ResourceScopeRef contextScope, ownerScope;
    private ResourceNamespace resourceNamespace = ResourceNamespace.Default;
    private SystemGraphPage? page, diagramPage;
    private int windowOffset;
    private bool systemsLoading, truncated, contextReady;
    private IReadOnlyDictionary<string, string>? overlayNotes;
    private SystemGraphNode? selected;
    private IReadOnlyList<SystemView> systems = [];
    private SystemView? system;
    private string T(string key) => Localizer[key];
    private bool reloadRequested;
    protected override void OnParametersSet()
    {
        showDiagram = RendererQuery != "list"; technicalDiagram = TechnicalQuery; summary = null; visualPage = null; selectedLink = null;
        cancellation?.Cancel();
        ++revision;
        loading = true; contextReady = false; page = null; diagramPage = null; selected = null; overlayNotes = null;
        system = null; systems = []; error = null; actionError = null;
        ResetPerspectiveState();
        reloadRequested = true;
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        // Prerender remains a fast loading shell. Fetch only once the interactive
        // renderer can paint batches and run the optional diagram island.
        if (!reloadRequested) return Task.CompletedTask;
        reloadRequested = false;
        return LoadAsync();
    }
    private Task RefreshAsync() => LoadAsync();
    private bool HasRoot => page?.Nodes.Any(node => node.Id == ActiveRoot) == true;
    private int WindowCapacity => HasRoot ? 79 : 80;
    private bool HasNextWindow => page is not null && page.Nodes.Count - (HasRoot ? 1 : 0) > windowOffset + WindowCapacity;
    private bool MutationBlocked => busy || loading || systemsLoading;
    private Task NextAsync() => ChangeWindow(WindowCapacity);
    private Task PreviousAsync() => ChangeWindow(-WindowCapacity);
    private Task ChangeWindow(int delta)
    {
        windowOffset = Math.Max(0, windowOffset + delta);
        selected = page?.Nodes.FirstOrDefault(node => node.Id == ActiveRoot);
        overlayNotes = null; diagramFailed = false;
        RefreshWindow();
        return Task.CompletedTask;
    }
    private void RefreshWindow()
    {
        if (page is null) { diagramPage = null; return; }
        var root = page.Nodes.FirstOrDefault(node => node.Id == ActiveRoot);
        var visible = page.Nodes.Where(node => node.Id != root?.Id).Skip(windowOffset).Take(root is null ? 80 : 79).ToArray();
        if (root is not null) visible = [root, .. visible];
        // Recorded pins keep their exact IDs/evidence. Their current authorized
        // catalogue label can be reused for presentation, including paired providers.
        var labels = page.Nodes.Where(node => node.Id == SystemGraphIds.Definition(node.Identity.ResourceUid)
            && !string.IsNullOrWhiteSpace(node.DisplayName)).ToDictionary(node => node.Identity.ResourceUid, node => node.DisplayName!);
        visible = visible.Select(node => string.IsNullOrWhiteSpace(node.DisplayName) && labels.TryGetValue(node.Identity.ResourceUid, out var label)
            ? node with { DisplayName = label } : node).ToArray();
        var ids = visible.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edges = page.Relations.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToArray();
        // Arrivals outside the current window and heartbeat timestamps do not restart layout.
        if (diagramPage is null || !diagramPage.Nodes.SequenceEqual(visible) || !diagramPage.Relations.SequenceEqual(edges))
            diagramPage = page with { Nodes = visible, Relations = edges };
        RefreshSummary();
    }
    private string NodeDisplayName(SystemGraphNode node) =>
        !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName
        : page?.Nodes.FirstOrDefault(candidate => candidate.Id == SystemGraphIds.Definition(node.Identity.ResourceUid))?.DisplayName
            is { Length: > 0 } label ? label : node.Identity.Name;
    private async Task LoadAsync()
    {
        cancellation?.Cancel(); cancellation?.Dispose();
        var pending = cancellation = new();
        var current = ++revision;
        var selectedSystem = system?.Uid;
        loading = true; overlayNotes = null; page = null; diagramPage = null; windowOffset = 0; truncated = false;
        selected = null; system = null; systems = []; error = null; actionError = null; diagramFailed = false;
        Task groups = Task.CompletedTask;
        var buffer = new SystemGraphStreamBuffer();
        ResetPerspectiveState(); summary = null; visualPage = null; selectedLink = null;
        try
        {
            var context = await ContextProvider.GetAsync(pending.Token);
            var actor = ResourceScopeRef.Parse(ContextQuery ?? context.SelectedScopeRef);
            var owner = ResourceScopeRef.Parse(OwnerQuery ?? actor.Value);
            if (current != revision) return;
            contextScope = actor; ownerScope = owner; contextReady = true;
            deploymentWorkspaces = context.Contexts.Where(value => value.Kind == Agentstration.Web.Components.State.ConsoleContextKind.Workspace).ToArray();
            namespaceInput = NamespaceQuery ?? ResourceNamespace.DefaultValue;
            var ns = ResourceNamespace.Parse(namespaceInput);
            resourceNamespace = ns;
            canWrite = context.Permissions.Contains("resources/write"); canDelete = context.Permissions.Contains("resources/delete");
            groups = LoadSystemsAsync(actor, owner, ns, selectedSystem, current, pending.Token);
            var roots = await PreparePerspectiveAsync(actor, ns, current, pending.Token);
            if (current != revision) return;
            var photos = new List<SystemGraphPage>();
            foreach (var root in roots)
            {
                buffer = new SystemGraphStreamBuffer();
                var rootNamespace = Perspective == "deployment" && configuration is not null ? assignment!.Namespace : ns;
                await foreach (var frame in Client.StreamAsync(actor, rootNamespace, root, pending.Token))
                {
                    if (current != revision) return;
                    if (!buffer.Apply(frame)) throw new InvalidOperationException("The graph observation was invalidated.");
                    if (frame.Kind == "heartbeat") continue;
                    received = SystemExplorerPresentation.Merge([.. photos, buffer.Page!]);
                    page = Perspective == "definition" && definitionRoots.Length > 0
                        ? SystemExplorerPresentation.Definition(received, definitionRoots) : received;
                    truncated |= buffer.Truncated;
                    selected ??= page.Nodes.FirstOrDefault(node => node.Id == ActiveRoot);
                    RefreshWindow();
                    await InvokeAsync(StateHasChanged);
                }
                if (!buffer.Complete) throw new InvalidOperationException("The graph observation ended before completion.");
                photos.Add(buffer.Page!);
            }

        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception)
        {
            if (current == revision)
            {
                buffer.Clear(); page = null; diagramPage = null; selected = null; overlayNotes = null;
                system = null; systems = []; ResetPerspectiveState(); error = T("LoadFailed");
            }
        }
        finally
        {
            if (current == revision) { loading = false; await InvokeAsync(StateHasChanged); }
            await groups;
        }
    }
    private async Task LoadSystemsAsync(ResourceScopeRef actor, ResourceScopeRef owner, ResourceNamespace ns,
        Guid? selectedSystem, long current, CancellationToken ct)
    {
        systemsLoading = true;
        try
        {
            var groups = await Client.ListAsync(actor, owner, ns, ct);
            if (current != revision || error is not null || ct.IsCancellationRequested) return;
            systems = groups;
            system = groups.FirstOrDefault(value => value.Uid == selectedSystem);
            renameLabel = system?.DisplayName ?? ""; confirmDelete = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { if (current == revision && error is null) actionError = T("SystemsLoadFailed"); }
        finally { if (current == revision) { systemsLoading = false; await InvokeAsync(StateHasChanged); } }
    }
    private Task Select(string id) { if (selected?.Id != id) overlayNotes = null; selected = page?.Nodes.FirstOrDefault(node => node.Id == id); return Task.CompletedTask; }
    private void OperationalChanged(Agentstration.SystemOperations.Contracts.SystemOperationalFacts? facts)
    {
        if (facts is null) { overlayNotes = null; return; }
        if (facts.NodeId != selected?.Id) return;
        string State(string state) => Localizer["OpsState_" + state.ToLowerInvariant()].ResourceNotFound ? T("OpsUnknown") : T("OpsState_" + state.ToLowerInvariant());
        var note = $"{T("OpsPublication")}: {State(facts.PublicationState)}; {T("OpsProvisioning")}: {State(facts.ProvisioningState)}; {T("OpsDependencies")}: {State(facts.DependencyState)}; {T("OpsWorkers")}: {State(facts.WorkerPresence)}. ";
        note += facts.Metrics is { } metrics ? $"{T("OpsActive")}: {metrics.ObservedActive}; {T("OpsFailed")}: {metrics.ObservedFailed}. {T("OpsPartial")}" : T("OpsNoMetrics");
        if (overlayNotes?.GetValueOrDefault(facts.NodeId) == note) return;
        overlayNotes = new Dictionary<string, string> { [facts.NodeId] = note };

    }
    private void DiagramFailed() => diagramFailed = true;
    private void ApplyNamespace() => Navigate(null, namespaceInput, ownerScope.Value);
    private void ChangeOwner(ChangeEventArgs args) => Navigate(RootQuery, resourceNamespace.Value, args.Value?.ToString());
    private void Navigate(string? root, string? ns, string? owner) => Navigation.NavigateTo(QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?>
    { ["context"] = ContextQuery ?? NavigationContext.ScopeRef ?? contextScope.Value, ["scopeRef"] = owner, ["namespace"] = ns, ["root"] = root, ["renderer"] = showDiagram ? null : "list" }));
    private string ReconciliationUrl(string root) => QueryHelpers.AddQueryString("/system-explorer/reconciliation", new Dictionary<string, string?>
    { ["context"] = contextScope.Value, ["namespace"] = RootNamespace(root), ["root"] = root });
    private string RootNamespace(string? root) => page?.Nodes.FirstOrDefault(node => node.Id == root)?.Identity.Namespace.Value ?? resourceNamespace.Value;
    private string ExplorerUrl(string? root, ResourceNamespace? ns = null) => QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?>
    { ["context"] = contextScope.Value, ["scopeRef"] = ownerScope.Value, ["namespace"] = ns?.Value ?? RootNamespace(root), ["root"] = root, ["renderer"] = showDiagram ? null : "list" });
    private void ChangeSystem(ChangeEventArgs args)
    {
        system = systems.FirstOrDefault(value => value.Uid.ToString() == args.Value?.ToString());
        renameLabel = system?.DisplayName ?? ""; confirmDelete = false;
    }
    private Task CreateAsync() => ChangeAsync(async token =>
    {
        return await Client.CreateAsync(contextScope, new($"system-{Guid.NewGuid():N}", newLabel, ScopeRef: ownerScope, Namespace: resourceNamespace), token);
    });
    private Task RenameAsync() => ChangeAsync(async token => await Client.RenameAsync(contextScope, system!, renameLabel, token));
    private Task AddMemberAsync() => ChangeAsync(async token =>
    {
        var identity = selected!.Identity;
        return await Client.AddMemberAsync(contextScope, system!, new(identity.ResourceUid, identity.OwnerScope, identity.Namespace, identity.Kind, identity.Name), token);
    });
    private Task RemoveMemberAsync(Guid uid) => ChangeAsync(async token => await Client.RemoveMemberAsync(contextScope, system!, uid, token));
    private Task DeleteAsync() => ChangeAsync(async token => { await Client.DeleteAsync(contextScope, system!, token); return null; });
    private async Task ChangeAsync(Func<CancellationToken, Task<SystemView?>> change)
    {
        if (MutationBlocked || cancellation is null) return;
        var current = revision;
        busy = true; actionError = null;
        try { var updated = await change(cancellation.Token); if (current == revision) { system = updated; await LoadAsync(); } }
        catch (OperationCanceledException) when (current != revision || cancellation.IsCancellationRequested) { }
        catch (Exception) { if (current == revision) actionError = T("ChangeFailed"); }
        finally { busy = false; }
    }
    private async Task ExportAsync()
    {
        if (page is null) return;
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Console.Components/system-explorer-export.js");
            await module.InvokeVoidAsync("download", JsonSerializer.Serialize(page, JsonOptions));
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { actionError = T("ExportFailed"); }
    }
    private string? DetailsUrl(SystemGraphNode node, bool designer = false)
    {
        var identity = node.Identity;
        if (identity.Kind == "AgentRevision")
        {
            var parentId = page?.Relations.FirstOrDefault(edge => edge.Kind == "publication" && edge.Target == node.Id)?.Source;
            var parent = page?.Nodes.FirstOrDefault(candidate => candidate.Id == parentId && candidate.Identity.Kind == "Agent");
            if (parent is null && catalog?.Revisions.Any(value => value.Id == node.Id) != true) return null;
            identity = parent?.Identity ?? catalog!.Agent.Identity;
        }
        var prefix = identity.Kind switch
        {
            "Agent" => "/agents/",
            "ModelProfile" => "/modelprofiles/",
            "Tool" => "/tools/",
            "ToolSet" => "/tools/sets/",
            "ToolProvider" => "/tools/providers/",
            "ToolDefinition" => "/tools/definitions/",
            "Schema" => "/schemas/",
            "Storage" => "/storages/",
            "StorageType" => "/storage-types/",
            "DataSource" => "/data-sources/",
            "DataSourceProfile" => "/data-source-profiles/",
            "KnowledgeSource" => "/knowledge-sources/",
            "ConnectedService" => "/connected-services/",
            "TransformationFunction" => "/transformation-functions/",
            "Trigger" => "/triggers/",
            "Flow" => identity.Namespace.IsDefault ? "/flows/" : $"/namespaces/{Uri.EscapeDataString(identity.Namespace.Value)}/flows/",
            _ => null
        };
        if (prefix is null) return null;
        return QueryHelpers.AddQueryString(prefix + Uri.EscapeDataString(identity.Name) + (designer ? "/designer" : ""), new Dictionary<string, string?>
        {
            ["context"] = contextScope.Value,
            ["scopeRef"] = identity.OwnerScope.Value,
            ["namespace"] = identity.Namespace.Value,
            ["version"] = node.Evidence?.Publication,
            ["uid"] = identity.ResourceUid.ToString()
        });
    }
    private IReadOnlyDictionary<string, string> RelationshipLabels
    {
        get
        {
            var labels = new[] { "system-membership", "exact-dependency", "dynamic-dependency", "generation-of", "publication", "relationship", "deployment-configuration", "deployed-revision", "toolset-access" }.ToDictionary(kind => kind, RelationKind);
            labels["runtime-awp-worker"] = T("DeploymentRuntime_awp-worker");
            labels["runtime-agentstration-server"] = T("DeploymentRuntime_agentstration-server");
            labels["runtime-remote-runtime"] = T("DeploymentRuntime_remote-runtime");
            labels["deployment-environment"] = T("C4Environment"); labels["deployment-desired"] = T("DeploymentDesired");
            labels["worker"] = T("C4Worker"); labels["recorded-generations"] = T("RecordedGenerations"); labels["current-generation"] = T("CurrentGeneration");
            foreach (var pair in summaryLabels) labels[pair.Key] = pair.Value;
            return labels;
        }
    }
    private string NodeState(string value) => T(value switch { "draft" => "Draft", "definition" => "Definition", "publication" => "Published", "published" => "Published", "recorded-generation" => "RecordedGeneration", "reference-identity" => "ReferenceIdentity", "recorded-deployment-configuration" => "DeploymentConfigurationState", _ => "Metadata" });
    private string RelationKind(string value) => T(value switch { "system-membership" => "Membership", "exact-dependency" => "Dependency", "dynamic-dependency" => "DynamicDependency", "generation-of" => "GenerationOf", "publication" => "Publication", "deployment-configuration" => "DeploymentConfigurationRelation", "deployed-revision" => "DeployedRevisionRelation", "toolset-access" => "SummaryToolSetAccess", _ => "Relation" });
    public void Dispose() { ++revision; cancellation?.Cancel(); cancellation?.Dispose(); GC.SuppressFinalize(this); }
}
