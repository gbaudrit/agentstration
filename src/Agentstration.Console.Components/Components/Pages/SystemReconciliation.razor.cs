using System.Text.Json;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.Pages;

public partial class SystemReconciliation
{
    [SupplyParameterFromQuery(Name = "context")] public string? ContextQuery { get; set; }
    [SupplyParameterFromQuery(Name = "namespace")] public string? NamespaceQuery { get; set; }
    [SupplyParameterFromQuery(Name = "root")] public string? RootQuery { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private CancellationTokenSource? cancellation;
    private long revision;
    private bool loading = true, busy, revalidated;
    private string? error, actionError;
    private string from = "", to = "";
    private ResourceScopeRef actor;
    private ResourceNamespace resourceNamespace = ResourceNamespace.Default;
    private IReadOnlyList<SystemGraphNode> candidates = [], consumers = [];
    private SystemGraphChangeResult? comparison;
    private ReconciliationPreview? review;
    private ReconciliationPreviewRequest? reviewedRequest;
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> versions = new(StringComparer.Ordinal);
    private string T(string key) => Localizer[key];
    private string ExplorerUrl => NavigationContext.AddToUri(QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?> { ["context"] = ContextQuery, ["namespace"] = NamespaceQuery, ["root"] = RootQuery }));
    protected override async Task OnParametersSetAsync()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        loading = true; error = null; candidates = []; ClearReview(); var current = revision;
        try
        {
            var context = await ContextProvider.GetAsync(token);
            var scope = ResourceScopeRef.Parse(ContextQuery ?? context.SelectedScopeRef);
            var ns = ResourceNamespace.Parse(NamespaceQuery ?? ResourceNamespace.DefaultValue);
            var page = await Inventory.ReadAsync(scope, ns, RootQuery, null, token);
            if (current != revision) return;
            actor = scope; resourceNamespace = ns; candidates = page.Nodes;
            from = page.Nodes.Any(node => node.Id == RootQuery) ? RootQuery! : ""; to = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (current == revision) error = T("LoadFailed"); }
        finally { if (current == revision) loading = false; }
    }
    private void ClearReview() { ++revision; comparison = null; consumers = []; review = null; reviewedRequest = null; selected.Clear(); versions.Clear(); actionError = null; revalidated = false; }
    private void Select(string id, bool value) { ++revision; if (value) selected.Add(id); else selected.Remove(id); review = null; reviewedRequest = null; revalidated = false; }
    private string Version(string id) => versions.GetValueOrDefault(id, "");
    private void ChangeVersion(string id, string? value) { ++revision; versions[id] = value ?? ""; review = null; reviewedRequest = null; revalidated = false; }
    private string NodeLabel(SystemGraphNode node) => $"{node.Identity.Kind} · {node.Identity.OwnerScope.Value} · {node.Identity.Namespace}/{node.Identity.Name} · {node.Evidence?.Publication ?? node.Evidence?.Generation?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "—"}";
    private Task CompareAsync() => RunAsync(async (token, current) =>
    {
        var result = await Client.CompareAsync(actor, resourceNamespace, from, to, token);
        if (current != revision) return;
        comparison = result; selected.Clear(); versions.Clear(); review = null; reviewedRequest = null; revalidated = false;
        consumers = result.Previous.Nodes.Concat(result.Current.Nodes).Where(node => node.Id != from && node.Id != to
            && node.Identity.Kind is "Agent" or "AgentRevision" or "Flow" or "ToolSet" or "ToolDefinition")
            .DistinctBy(node => node.Id).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
    });
    private Task PreviewAsync() => RunAsync(async (token, current) =>
    {
        var request = new ReconciliationPreviewRequest(from, to, selected.Order(StringComparer.Ordinal).ToArray(), resourceNamespace,
            versions.Where(pair => selected.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        var result = await Client.PreviewAsync(actor, request, token);
        if (current != revision) return;
        reviewedRequest = request; review = result; revalidated = false;
    });
    private Task RevalidateAsync() => RunAsync(async (token, current) =>
    {
        if (reviewedRequest is null || review is null) return;
        var fresh = await Client.RevalidateAsync(actor, new(reviewedRequest, review.Digest), token);
        if (current != revision) return;
        review = fresh; revalidated = true;
    });
    private async Task RunAsync(Func<CancellationToken, long, Task> operation)
    {
        if (busy || cancellation is null) return;
        busy = true; actionError = null; var current = revision;
        try { await operation(cancellation.Token, current); }
        catch (OperationCanceledException) when (current != revision || cancellation.IsCancellationRequested) { }
        catch (Exception) { if (current == revision) { review = null; reviewedRequest = null; revalidated = false; actionError = T("ReconFailed"); } }
        finally { busy = false; }
    }
    private string Action(string action) => T(action switch { "no-op" => "ReconNoOp", "adopt" => "ReconAdopt", "publish" => "ReconPublish", _ => "ReconReview" });
    private string Status(string status) => T(status switch { "no-op" => "ReconNoOp", "blocked" => "ReconBlocked", _ => "ReconUnknown" });
    private string Reason(string reason) => T(reason switch
    {
        "compatible-dynamic-no-republication" => "ReconCompatible",
        "same-observation" => "ReconSame",
        "contract-mismatch" => "ReconMismatch",
        "agent-adoption-policy-unavailable" => "ReconAgentUnavailable",
        "conditional-family-command-unavailable" => "ReconConditionalUnavailable",
        "source-unavailable" or "selection-unavailable" => "ReconUnavailable",
        "dependency-cycle" => "ReconCycle",
        "analysis-truncated" or "authoring-evidence-truncated" => "ReconTruncated",
        "partial-coverage" => "ReconPartial",
        "execution-not-available" => "ReconReadOnly",
        _ => "ReconUnknown"
    });
    public void Dispose() { ++revision; cancellation?.Cancel(); cancellation?.Dispose(); GC.SuppressFinalize(this); }
}
