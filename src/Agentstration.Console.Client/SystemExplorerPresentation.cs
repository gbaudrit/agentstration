using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Web.Console;

/// <summary>Filters an already authorized observation for explicit UI selections; grants no access.</summary>
public static class SystemExplorerPresentation
{
    public static SystemGraphPage Merge(IReadOnlyList<SystemGraphPage> photos)
    {
        var last = photos[^1]; var nodes = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        foreach (var photo in photos)
            foreach (var node in photo.Nodes)
            {
                if (nodes.TryGetValue(node.Id, out var previous) && previous != node) throw new InvalidOperationException("The comparison observations changed.");
                nodes[node.Id] = node;
            }
        return last with
        {
            Nodes = nodes.Values.ToArray(),
            Relations = photos.SelectMany(photo => photo.Relations).Distinct().ToArray(),
            Scopes = photos.SelectMany(photo => photo.Scopes).DistinctBy(scope => scope.Scope).ToArray(),
            Snapshot = photos.Count == 1 ? last.Snapshot : "comparison:" + string.Join("|", photos.Select(photo => photo.Snapshot)),
            GloballyComplete = false,
            HasMore = false,
            NextCursor = null
        };
    }
    public static SystemGraphPage Definition(SystemGraphPage page, IReadOnlyList<string> roots)
    {
        var selected = roots.Where(id => page.Nodes.Any(node => node.Id == id)).ToHashSet(StringComparer.Ordinal);
        var edges = page.Relations.Where(edge => edge.Kind is not ("publication" or "deployment-configuration" or "deployed-revision")).ToLookup(edge => edge.Source);
        var queue = new Queue<string>(selected);
        while (queue.TryDequeue(out var id))
            foreach (var edge in edges[id]) if (selected.Add(edge.Target)) queue.Enqueue(edge.Target);
        return page with
        {
            Nodes = page.Nodes.Where(node => selected.Contains(node.Id)).OrderBy(node => Array.IndexOf(roots.ToArray(), node.Id) is var index && index >= 0 ? index : int.MaxValue).ToArray(),
            Relations = page.Relations.Where(edge => selected.Contains(edge.Source) && selected.Contains(edge.Target)).ToArray(),
            Coverage = "selected-definition-observation",
            GloballyComplete = false
        };
    }
}
