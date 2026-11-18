using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Reconciliation.Contracts;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Reconciliation;

public sealed class ReconciliationPreviewException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Read-only, selected reconciliation review. Never owns publication or Agent-adoption policy.</summary>
public sealed class ReconciliationPreviewService(ISystemGraphImpactService impact, ISystemGraphQueryService inventory,
    ISystemGraphAuthorization authorization) : IReconciliationPreviewService
{
    public async Task<ReconciliationPreview> PreviewAsync(ReconciliationPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidId(request.From) || !ValidId(request.To) || request.Consumers is null || request.Consumers.Count is < 1 or > 32
            || request.Consumers.Any(id => !ValidId(id)) || request.TargetVersions?.Count > 32
            || request.TargetVersions?.Any(pair => !request.Consumers.Contains(pair.Key, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 128 || pair.Value.Any(char.IsControl)) == true)
            throw new ReconciliationPreviewException("reconciliation_request_invalid");
        var consumers = request.Consumers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var versions = request.TargetVersions?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var context = await authorization.GetContextAsync(ct);
        var comparison = await impact.CompareAsync(new(request.From, request.To, request.Namespace, 32, 1000), ct);
        var blockers = new SortedSet<string>(StringComparer.Ordinal) { "partial-coverage", "execution-not-available" };
        var nodes = comparison.Previous.Nodes.Concat(comparison.Current.Nodes).GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var selected = consumers.Where(nodes.ContainsKey).ToHashSet(StringComparer.Ordinal);
        if (comparison.Assessment == "unavailable") blockers.Add("source-unavailable");
        if (selected.Count != consumers.Length) blockers.Add("selection-unavailable");
        if (comparison.Previous.Truncated || comparison.Current.Truncated) blockers.Add("analysis-truncated");
        if (comparison.Previous.HasCycles || comparison.Current.HasCycles) blockers.Add("dependency-cycle");
        var before = comparison.Previous.Paths.Where(path => selected.Contains(path.Nodes[^1])).ToArray();
        var after = comparison.Current.Paths.Where(path => selected.Contains(path.Nodes[^1])).ToArray();
        var paths = before.Concat(after).ToArray();
        var retainedIds = paths.SelectMany(path => path.Nodes).ToHashSet(StringComparer.Ordinal);
        foreach (var root in new[] { request.From, request.To }) if (nodes.ContainsKey(root)) retainedIds.Add(root);
        var edges = paths.SelectMany(path => path.Relations).Distinct().ToList();
        var authoringSources = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        // Current authoring identities are distinct from selected immutable publications. Read only
        // bounded authorized pages; require one observation while constructing this review.
        foreach (var consumer in selected.Order(StringComparer.Ordinal))
        {
            var node = nodes[consumer];
            var sourceId = SystemGraphIds.Definition(node.Identity.ResourceUid);
            var page = await inventory.QueryAsync(new(request.Namespace, node.Identity.Kind == "AgentRevision" ? consumer : sourceId, 200), ct);
            if (node.Identity.Kind == "AgentRevision")
            {
                // Use the authorized native publication relationship, never a revision-name join.
                var parentIds = page.Relations.Where(edge => edge.Kind == "publication" && edge.Target == consumer).Select(edge => edge.Source).ToHashSet(StringComparer.Ordinal);
                var parents = page.Nodes.Where(candidate => candidate.Identity.Kind == "Agent" && parentIds.Contains(candidate.Id)).ToArray();
                sourceId = parents.Length == 1 ? parents[0].Id : "";
            }
            if (page.HasMore) blockers.Add("authoring-evidence-truncated");
            var source = page.Nodes.FirstOrDefault(candidate => candidate.Id == sourceId);
            if (source is null) blockers.Add("authoring-evidence-unavailable");
            else { nodes[source.Id] = source; retainedIds.Add(source.Id); authoringSources[consumer] = source; }
            edges.AddRange(page.Relations.Where(edge => retainedIds.Contains(edge.Source) && retainedIds.Contains(edge.Target)));
        }
        // Neighbor pages have their own slice digests. Recompare the full authorized
        // observation after all authoring reads instead of equating unlike slice hashes.
        var finalObservation = await impact.CompareAsync(new(request.From, request.To, request.Namespace, 32, 1000), ct);
        var finalContext = await authorization.GetContextAsync(ct);
        if (finalObservation.Current.Snapshot != comparison.Current.Snapshot || finalContext.AuthorizationKey != context.AuthorizationKey)
            throw new ReconciliationPreviewException("reconciliation_observation_changed");
        var steps = new List<ReconciliationPreviewStep>();
        foreach (var consumer in DependencyOrder(selected, paths, blockers))
        {
            ct.ThrowIfCancellationRequested();
            var node = nodes[consumer];
            var consumerPaths = after.Where(path => path.Nodes[^1] == consumer).ToArray();
            var compatible = consumerPaths.Length > 0 && consumerPaths.All(path => path.Assessment == "compatible-dynamic-no-republication");
            var mismatch = consumerPaths.Any(path => path.Assessment == "contract-mismatch");
            var (action, status, reason) = compatible ? ("no-op", "no-op", "compatible-dynamic-no-republication")
                : mismatch ? ("publish", "blocked", "contract-mismatch")
                : comparison.Assessment == "same-observation" ? ("no-op", "no-op", "same-observation")
                : node.Identity.Kind is "Agent" or "AgentRevision" ? ("adopt", "blocked", "agent-adoption-policy-unavailable")
                : node.Identity.Kind is "ToolDefinition" or "ToolSet" or "Flow" ? ("publish", "blocked", "conditional-family-command-unavailable")
                : ("review", "unknown", "family-action-unsupported");
            if (status is "blocked" or "unknown") blockers.Add(reason);
            versions.TryGetValue(consumer, out var targetVersion);
            steps.Add(new(consumer, node.Identity, action, status, reason, targetVersion,
                before.SelectMany(path => path.Relations).Where(edge => edge.Source == consumer).Distinct().OrderBy(edge => edge.SourceField, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal).ToArray(),
                after.SelectMany(path => path.Relations).Where(edge => edge.Source == consumer).Distinct().OrderBy(edge => edge.SourceField, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal).ToArray())
            { AuthoringSource = authoringSources.GetValueOrDefault(consumer) });
        }
        var readSet = retainedIds.Where(nodes.ContainsKey).Select(id => nodes[id]).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var relations = edges.Where(edge => retainedIds.Contains(edge.Source) && retainedIds.Contains(edge.Target)).Distinct()
            .OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceField, StringComparer.Ordinal).ThenBy(edge => JsonSerializer.Serialize(edge), StringComparer.Ordinal).ToArray();
        // Bind only relevant authorized evidence and explicit choices, never the global graph digest
        // or sample time. Unrelated changes therefore need not invalidate an unchanged review.
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            context.AuthorizationKey,
            request.From,
            request.To,
            request.Namespace,
            Consumers = consumers,
            TargetVersions = versions,
            ReadSet = readSet,
            Relations = relations,
            Steps = steps,
            Blockers = blockers.ToArray()
        })));
        return new(digest, finalObservation.Current.ObservedAt, comparison.Assessment, readSet, relations, steps, blockers.ToArray(), false, false);
    }
    public async Task<ReconciliationPreview> RevalidateAsync(ReconciliationRevalidationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedDigest is null || request.ExpectedDigest.Length != 64 || !request.ExpectedDigest.All(Uri.IsHexDigit))
            throw new ReconciliationPreviewException("reconciliation_request_invalid");
        var fresh = await PreviewAsync(request.Request, ct);
        if (!string.Equals(fresh.Digest, request.ExpectedDigest, StringComparison.Ordinal)) throw new ReconciliationPreviewException("reconciliation_review_stale");
        return fresh;
    }
    private static IReadOnlyList<string> DependencyOrder(HashSet<string> selected, SystemGraphImpactPath[] paths, SortedSet<string> blockers)
    {
        var requirements = selected.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var preceding = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in path.Nodes.Where(selected.Contains))
            {
                requirements[id].UnionWith(preceding);
                preceding.Add(id);
            }
        }
        var pending = new SortedSet<string>(selected, StringComparer.Ordinal);
        var result = new List<string>();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(id => !requirements[id].Overlaps(pending));
            if (ready is null) { blockers.Add("dependency-cycle"); result.AddRange(pending); break; }
            pending.Remove(ready); result.Add(ready);
        }
        return result;
    }
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 512;
}
