using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Web.Console;

public sealed record ResourceRelationsView(SystemExplorerSummary Summary, bool CanExpand, bool Limited)
{
    public IReadOnlyList<string> Kinds { get; init; } = [];
    public int TotalCount { get; init; }
    public int HiddenCount { get; init; }
}
public sealed record ResourceRelationsFocus(ResourceScopeRef Context, ResourceNamespace Namespace, string Root, SystemGraphDirection Direction, int Depth);

/// <summary>Bounds a presentation of received, authorized metadata. Never discovers grants.</summary>
public static class ResourceRelationsPresentation
{
    public const int NodeLimit = 80;
    public const int MaximumDepth = 8;

    public static ResourceRelationsView Create(SystemGraphPage observation, string root, SystemGraphDirection direction, int depth, IReadOnlySet<string>? hiddenKinds = null)
    {
        if (direction is not (SystemGraphDirection.Dependencies or SystemGraphDirection.Usages)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (depth is < 1 or > MaximumDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        var summary = SystemExplorerSummaries.Create(observation, observation, root,
            restrictAgentAccess: direction == SystemGraphDirection.Dependencies);
        var outgoing = summary.Graph.Relations.ToLookup(edge => direction == SystemGraphDirection.Usages ? edge.Target : edge.Source);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var queue = new Queue<(string Id, int Depth)>();
        if (summary.Graph.Nodes.Any(node => node.Id == root)) { selected.Add(root); order.Add(root); queue.Enqueue((root, 0)); }
        var expandable = false;
        while (queue.TryDequeue(out var current))
            foreach (var edge in outgoing[current.Id].OrderBy(edge => direction == SystemGraphDirection.Usages ? edge.Source : edge.Target, StringComparer.Ordinal))
            {
                var next = direction == SystemGraphDirection.Usages ? edge.Source : edge.Target;
                if (selected.Contains(next)) continue;
                if (current.Depth == depth) { expandable = true; continue; }
                selected.Add(next); order.Add(next); queue.Enqueue((next, current.Depth + 1));
            }
        var nodes = summary.Graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var totalCount = order.Count;
        var filtered = order.Where(id => id == root || hiddenKinds?.Contains(nodes[id].Identity.Kind) != true).ToArray();
        var limited = filtered.Length > NodeLimit;
        order = filtered.Take(NodeLimit).ToList();
        selected = order.ToHashSet(StringComparer.Ordinal);
        var graph = summary.Graph with
        {
            Nodes = order.Select(id => nodes[id]).ToArray(),
            Relations = summary.Graph.Relations.Where(edge => selected.Contains(edge.Source) && selected.Contains(edge.Target)).ToArray(),
            GloballyComplete = false
        };
        return new(summary with
        {
            Graph = graph,
            Links = summary.Links.Where(link => selected.Contains(link.Source.Id) && selected.Contains(link.Target.Id)).ToArray(),
            RepresentedNodes = summary.RepresentedNodes.Where(pair => selected.Contains(pair.Key)).ToDictionary()
        }, expandable && depth < MaximumDepth && !limited, limited)
        {
            Kinds = summary.Graph.Nodes.Select(node => node.Identity.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            TotalCount = totalCount,
            HiddenCount = totalCount - filtered.Length
        };
    }
}
