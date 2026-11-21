using Agentstration.SystemGraph.Contracts;

namespace Agentstration.SystemGraph;

// Receives only a validated authorized observation. Never queries the privileged family ports.
internal static class SystemGraphImpactAnalyzer
{
    internal static SystemGraphImpactResult Analyze(SystemGraphImpactQuery query, SystemGraphNode[] nodes,
        SystemGraphRelation[] relations, string snapshot, DateTimeOffset observedAt, CancellationToken ct)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var paths = new List<SystemGraphImpactPath>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var cycles = false;
        if (byId.ContainsKey(query.Root))
        {
            selected.Add(query.Root);
            // Publication membership follows definition -> publication; dependency and generation
            // relations point consumer -> dependency and are traversed in reverse.
            var index = relations.Where(edge => edge.Kind is "exact-dependency" or "generation-of" or "publication" or "dynamic-dependency")
                .Select(edge => (From: edge.Kind == "publication" ? edge.Source : edge.Target,
                    To: edge.Kind == "publication" ? edge.Target : edge.Source, Edge: edge))
                .GroupBy(edge => edge.From, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.To, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Edge.SourceField, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var queue = new Queue<SystemGraphImpactPath>();
            queue.Enqueue(new([query.Root], [], "root"));
            while (queue.TryDequeue(out var path))
            {
                ct.ThrowIfCancellationRequested();
                if (!index.TryGetValue(path.Nodes[^1], out var next)) continue;
                foreach (var edge in next)
                {
                    ct.ThrowIfCancellationRequested();
                    if (path.Nodes.Contains(edge.To, StringComparer.Ordinal)) { cycles = true; continue; }
                    if (path.Relations.Count >= query.MaximumDepth || paths.Count >= query.MaximumPaths)
                    { truncated = true; continue; }
                    var target = byId[edge.To];
                    var assessment = edge.Edge.Assessment ?? edge.Edge.Kind switch
                    {
                        "generation-of" when byId[edge.From].Evidence?.Generation is { } current && target.Evidence?.Generation is { } pinned && current != pinned => "generation-drift",
                        "publication" => "historical-publication",
                        "dynamic-dependency" => "dynamic-selection-review",
                        _ => "recorded-dependency-review"
                    };
                    if (path.Assessment is "compatible-dynamic-no-republication" or "contract-mismatch") assessment = path.Assessment;
                    var expanded = new SystemGraphImpactPath([.. path.Nodes, edge.To], [.. path.Relations, edge.Edge], assessment);
                    paths.Add(expanded);
                    selected.Add(edge.To);
                    queue.Enqueue(expanded);
                }
            }
        }
        return new(nodes.Where(node => selected.Contains(node.Id)).ToArray(), paths, snapshot, observedAt,
            truncated, cycles, false, ["authorized-partial-observation", "unsupported-or-unresolved-relations-possible", "compatibility-not-certified", "not-an-execution-plan"]);
    }
}
