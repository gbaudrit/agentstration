using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.SystemExplorer;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components.ResourceRelations;

public partial class ResourceRelations
{
    [Parameter, EditorRequired] public ResourceScopeRef Context { get; set; }
    [Parameter, EditorRequired] public ResourceNamespace Namespace { get; set; } = ResourceNamespace.Default;
    [Parameter, EditorRequired] public string Root { get; set; } = string.Empty;
    [Parameter] public SystemGraphDirection InitialDirection { get; set; } = SystemGraphDirection.Dependencies;
    [Parameter] public EventCallback<SystemGraphNode> SelectedChanged { get; set; }
    [Parameter] public EventCallback<SystemGraphNode> OpenResource { get; set; }
    [Parameter] public EventCallback<ResourceRelationsFocus> OpenExplorer { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private (ResourceScopeRef Context, ResourceNamespace Namespace, string Root, SystemGraphDirection Direction)? parameters;
    private CancellationTokenSource? cancellation;
    private long requestVersion;
    private bool reload, disposed, loading = true, failed, truncated, diagramFailed, showDiagram = true;
    private string focusRoot = string.Empty;
    private ResourceNamespace focusNamespace;
    private SystemGraphDirection direction;
    private int depth = 1;
    private SystemGraphPage? observation;
    private ResourceRelationsView? view;
    private SystemGraphNode? selected;
    private SystemExplorerLink? selectedLink;
    private IReadOnlyDictionary<string, string> relationshipLabels = new Dictionary<string, string>();
    private SystemGraphDiagramOptions? diagramOptions;
    private IReadOnlyDictionary<string, string>? rootNote;
    private long diagramKey;
    private IReadOnlyDictionary<string, string> viewportLabels => new Dictionary<string, string>
    { ["controls"] = T("RelationsViewport"), ["in"] = T("RelationsZoomIn"), ["out"] = T("RelationsZoomOut"), ["fit"] = T("RelationsFit") };
    private string T(string key) => Localizer[key];
    private string Name(SystemGraphNode node) => node.DisplayName
        ?? observation?.Nodes.FirstOrDefault(value => value.Id == SystemGraphIds.Definition(node.Identity.ResourceUid))?.DisplayName
        ?? node.Identity.Name;
    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    protected override void OnParametersSet()
    {
        var next = (Context, Namespace, Root, InitialDirection);
        if (parameters == next) return;
        parameters = next;
        cancellation?.Cancel(); ++requestVersion;
        focusRoot = Root; focusNamespace = Namespace;
        direction = InitialDirection; depth = 1;
        Clear(); reload = true;
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (!reload) return Task.CompletedTask;
        reload = false;
        return LoadAsync();
    }
    private void Clear()
    {
        observation = null; view = null; selected = null; selectedLink = null;
        failed = false; loading = true; truncated = false; diagramFailed = false; ++diagramKey;
    }
    public Task RefreshAsync() => LoadAsync();
    private async Task LoadAsync()
    {
        if (disposed) return;
        cancellation?.Cancel(); cancellation?.Dispose();
        var pending = cancellation = new();
        var current = ++requestVersion;
        Clear();
        if (string.IsNullOrWhiteSpace(focusRoot) || string.IsNullOrWhiteSpace(Context.Value)
            || direction is not (SystemGraphDirection.Dependencies or SystemGraphDirection.Usages))
        { failed = true; loading = false; await InvokeAsync(StateHasChanged); return; }
        var buffer = new SystemGraphStreamBuffer();
        try
        {
            await foreach (var frame in Client.StreamAsync(Context, new(focusNamespace, focusRoot, MaximumDepth: 32, Direction: direction), pending.Token))
            {
                if (disposed || current != requestVersion || pending.IsCancellationRequested) return;
                if (!buffer.Apply(frame)) throw new InvalidOperationException("The graph observation was invalidated.");
                if (frame.Kind != "heartbeat")
                {
                    observation = buffer.Page; truncated = buffer.Truncated;
                    RefreshView();
                    await InvokeAsync(StateHasChanged);
                }
            }
            if (!buffer.Complete) throw new InvalidOperationException("The graph observation did not complete.");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception)
        {
            if (disposed || current != requestVersion) return;
            Clear(); failed = true;
        }
        finally
        {
            if (!disposed && current == requestVersion) { loading = false; await InvokeAsync(StateHasChanged); }
        }
    }
    private void RefreshView()
    {
        if (observation is null) { view = null; return; }
        var next = ResourceRelationsPresentation.Create(observation, focusRoot, direction, depth);
        var labels = next.Summary.Links.ToDictionary(link => link.Id, LinkTitle, StringComparer.Ordinal);
        // Heartbeats and arrivals outside the displayed neighborhood never restart layout.
        if (view is null || !view.Summary.Graph.Nodes.SequenceEqual(next.Summary.Graph.Nodes)
            || !view.Summary.Graph.Relations.SequenceEqual(next.Summary.Graph.Relations)
            || !relationshipLabels.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
        {
            view = next; relationshipLabels = labels;
            diagramOptions = new("resource", focusRoot);
            rootNote = new Dictionary<string, string> { [focusRoot] = T("RelationsRoot") };
        }
        else view = next with { Summary = next.Summary with { Graph = view.Summary.Graph } };
        selected = next.Summary.Graph.Nodes.FirstOrDefault(node => node.Id == selected?.Id)
            ?? next.Summary.Graph.Nodes.FirstOrDefault(node => node.Id == focusRoot);
        selectedLink = next.Summary.Links.FirstOrDefault(link => link.Id == selectedLink?.Id);
    }
    private string LinkTitle(SystemExplorerLink link) => SystemExplorerLinkLabels.Title(link, Localizer, Name);
    private Task ChangeDirectionAsync(SystemGraphDirection value) { direction = value; depth = 1; return LoadAsync(); }
    private void ChangeDepth(int value) { depth = Math.Clamp(value, 1, ResourceRelationsPresentation.MaximumDepth); RefreshView(); }
    private void Expand() => ChangeDepth(depth + 1);
    private async Task SelectNodeAsync(string id)
    {
        var node = view?.Summary.Graph.Nodes.FirstOrDefault(value => value.Id == id);
        if (node is null || disposed) return;
        selected = node; selectedLink = null;
        await SelectedChanged.InvokeAsync(node);
    }
    private void SelectLink(string id) => selectedLink = view?.Summary.Links.FirstOrDefault(link => link.Id == id);
    private Task RecenterAsync()
    {
        if (selected is null) return Task.CompletedTask;
        focusRoot = selected.Id; focusNamespace = selected.Identity.Namespace; depth = 1;
        return LoadAsync();
    }
    private Task ResetFocusAsync()
    {
        focusRoot = Root; focusNamespace = Namespace; depth = 1;
        return LoadAsync();
    }
    public void Dispose()
    {
        disposed = true; ++requestVersion; cancellation?.Cancel(); cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}
