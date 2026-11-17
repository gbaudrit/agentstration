using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
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
    private SystemGraphPage? page;
    private SystemGraphNode? selected;
    private IReadOnlyList<SystemView> systems = [];
    private SystemView? system;
    private string T(string key) => Localizer[key];
    protected override Task OnParametersSetAsync()
    {
        showDiagram = RendererQuery != "list";
        return LoadAsync(null);
    }
    private Task RefreshAsync() => LoadAsync(null);
    private Task NextAsync() => LoadAsync(page?.NextCursor);
    private async Task LoadAsync(string? cursor)
    {
        cancellation?.Cancel(); cancellation?.Dispose();
        var pending = cancellation = new();
        var current = ++revision;
        var selectedSystem = system?.Uid;
        loading = true; page = null; selected = null; system = null; systems = []; error = null; actionError = null; diagramFailed = false;
        try
        {
            var context = await ContextProvider.GetAsync(pending.Token);
            var actor = ResourceScopeRef.Parse(ContextQuery ?? context.SelectedScopeRef);
            var owner = ResourceScopeRef.Parse(OwnerQuery ?? actor.Value);
            var ns = ResourceNamespace.Parse(NamespaceQuery ?? ResourceNamespace.DefaultValue);
            var graph = await Client.ReadAsync(actor, ns, RootQuery, cursor, pending.Token);
            var groups = await Client.ListAsync(actor, owner, ns, pending.Token);
            if (current != revision) return;
            contextScope = actor; ownerScope = owner; resourceNamespace = ns; namespaceInput = ns.Value;
            canWrite = context.Permissions.Contains("resources/write"); canDelete = context.Permissions.Contains("resources/delete");
            page = graph; systems = groups;
            selected = graph.Nodes.FirstOrDefault(node => node.Id == RootQuery);
            system = groups.FirstOrDefault(value => value.Uid == selectedSystem);
            renameLabel = system?.DisplayName ?? "";
            confirmDelete = false;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception) { if (current == revision) error = T("LoadFailed"); }
        finally { if (current == revision) loading = false; }
    }
    private Task Select(string id) { selected = page?.Nodes.FirstOrDefault(node => node.Id == id); return Task.CompletedTask; }
    private void DiagramFailed() => diagramFailed = true;
    private void ApplyNamespace() => Navigate(null, namespaceInput, ownerScope.Value);
    private void ChangeOwner(ChangeEventArgs args) => Navigate(RootQuery, resourceNamespace.Value, args.Value?.ToString());
    private void Navigate(string? root, string? ns, string? owner) => Navigation.NavigateTo(QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?>
    { ["context"] = ContextQuery ?? NavigationContext.ScopeRef ?? contextScope.Value, ["scopeRef"] = owner, ["namespace"] = ns, ["root"] = root, ["renderer"] = showDiagram ? null : "list" }));
    private string ReconciliationUrl(string root) => QueryHelpers.AddQueryString("/system-explorer/reconciliation", new Dictionary<string, string?>
    { ["context"] = contextScope.Value, ["namespace"] = resourceNamespace.Value, ["root"] = root });
    private string ExplorerUrl(string? root) => QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?>
    { ["context"] = contextScope.Value, ["scopeRef"] = ownerScope.Value, ["namespace"] = resourceNamespace.Value, ["root"] = root, ["renderer"] = showDiagram ? null : "list" });
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
        if (busy || cancellation is null) return;
        var current = revision;
        busy = true; actionError = null;
        try { var updated = await change(cancellation.Token); if (current == revision) { system = updated; await LoadAsync(null); } }
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
            if (parent is null) return null;
            identity = parent.Identity;
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
    private IReadOnlyDictionary<string, string> RelationshipLabels => new[] { "system-membership", "exact-dependency", "dynamic-dependency", "generation-of", "publication", "relationship" }.ToDictionary(kind => kind, RelationKind);
    private string NodeState(string value) => T(value switch { "draft" => "Draft", "definition" => "Definition", "publication" => "Published", "published" => "Published", "recorded-generation" => "RecordedGeneration", "reference-identity" => "ReferenceIdentity", _ => "Metadata" });
    private string RelationKind(string value) => T(value switch { "system-membership" => "Membership", "exact-dependency" => "Dependency", "dynamic-dependency" => "DynamicDependency", "generation-of" => "GenerationOf", "publication" => "Publication", _ => "Relation" });
    public void Dispose() { ++revision; cancellation?.Cancel(); cancellation?.Dispose(); GC.SuppressFinalize(this); }
}
