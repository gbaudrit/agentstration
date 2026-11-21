using System.Security.Cryptography;
using System.Text;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Web.Console;

public sealed record SystemExplorerLinkPath(string Kind, SystemGraphNode? Via, IReadOnlyList<SystemGraphRelation> Steps);
public sealed record SystemExplorerLink(string Id, SystemGraphNode Source, SystemGraphNode Target, IReadOnlyList<SystemExplorerLinkPath> Paths)
{
    public IReadOnlyList<SystemGraphNode> ToolDefinitions { get; init; } = [];
    public IReadOnlyList<SystemGraphRelation> Provenance { get; init; } = [];
}
public sealed record SystemExplorerSummary(SystemGraphPage Graph, IReadOnlyList<SystemExplorerLink> Links,
    IReadOnlyDictionary<string, IReadOnlyList<SystemGraphNode>> RepresentedNodes);

/// <summary>A presentation of already authorized metadata; never computes permissions or execution material.</summary>
public static class SystemExplorerSummaries
{
    public static SystemExplorerSummary Create(SystemGraphPage observation, SystemGraphPage window, string? root, bool restrictAgentAccess = true)
    {
        var all = observation.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var visible = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        var representatives = new Dictionary<string, string>(StringComparer.Ordinal);
        var represented = new Dictionary<string, IReadOnlyList<SystemGraphNode>>(StringComparer.Ordinal);
        static bool Intermediate(SystemGraphNode node) => node.Identity.Kind is "ToolSet" or "ToolDefinition";
        static string GroupKey(SystemGraphNode node) => node.Deployment is not null || node.State == "publication" ? node.Id : node.Identity.ResourceUid.ToString("D");
        var completeGroups = observation.Nodes.ToLookup(GroupKey);
        foreach (var group in window.Nodes.GroupBy(GroupKey))
        {
            var members = group.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
            var primary = members.FirstOrDefault(node => node.Id == root)
                ?? members.FirstOrDefault(node => node.State == "recorded-generation")
                ?? members.FirstOrDefault(node => node.State == "definition") ?? members[0];
            var completeMembers = completeGroups[group.Key].ToArray();
            foreach (var member in completeMembers) representatives[member.Id] = primary.Id;
            if (Intermediate(primary) && primary.Id != root) continue;
            var label = members.FirstOrDefault(node => !string.IsNullOrWhiteSpace(node.DisplayName))?.DisplayName;
            visible[primary.Id] = label is null ? primary : primary with { DisplayName = label };
            represented[primary.Id] = completeMembers;
        }
        var paths = new Dictionary<(string Source, string Target), List<SystemExplorerLinkPath>>();
        void Add(string source, string target, SystemExplorerLinkPath path)
        {
            if (!representatives.TryGetValue(source, out var from) || !representatives.TryGetValue(target, out var to)
                || from == to || !visible.ContainsKey(from) || !visible.ContainsKey(to)) return;
            if (!paths.TryGetValue((from, to), out var list)) paths[(from, to)] = list = [];
            if (!list.Any(previous => previous.Kind == path.Kind && previous.Via?.Id == path.Via?.Id && previous.Steps.SequenceEqual(path.Steps))) list.Add(path);
        }
        foreach (var edge in observation.Relations)
        {
            if (edge.Kind == "toolset-access")
            {
                if (edge.ViaNodeId is not { } via || !all.TryGetValue(via, out var toolSet)) continue;
                Add(edge.Source, edge.Target, new("toolset", toolSet, [edge]));
            }
            else
            {
                var direct = all.TryGetValue(edge.Source, out var source) && source.Identity.Kind is "Agent" or "AgentRevision"
                    && all.TryGetValue(edge.Target, out var target) && target.Identity.Kind == "Tool"
                    && edge.Kind == "declared-reference" && edge.SourceField?.StartsWith("definition.tools[", StringComparison.Ordinal) == true;
                Add(edge.Source, edge.Target, new(direct ? "direct" : edge.Kind, null, [edge]));
            }
        }
        // ToolDefinition nodes are provenance. Follow their typed outgoing metadata edges,
        // never reverse publications or ToolSet memberships to invent an Agent grant.
        var outgoing = observation.Relations.Where(edge => edge.Kind is not ("publication" or "generation-of" or "toolset-access")).ToLookup(edge => edge.Source);
        foreach (var tool in window.Nodes.Where(node => node.Identity.Kind == "Tool"))
        {
            var queue = new Queue<(string Id, SystemGraphRelation[] Steps, HashSet<string> Seen)>();
            foreach (var first in outgoing[tool.Id])
                if (all.TryGetValue(first.Target, out var next) && next.Identity.Kind == "ToolDefinition")
                    queue.Enqueue((next.Id, [first], new(StringComparer.Ordinal) { tool.Id, next.Id }));
            var examined = 0;
            while (queue.TryDequeue(out var current) && examined++ < 128)
            {
                if (current.Steps.Length >= 8) continue;
                foreach (var edge in outgoing[current.Id])
                {
                    if (!all.TryGetValue(edge.Target, out var next) || current.Seen.Contains(next.Id)) continue;
                    var steps = current.Steps.Append(edge).ToArray();
                    if (next.Identity.Kind == "ToolDefinition")
                        queue.Enqueue((next.Id, steps, new(current.Seen, StringComparer.Ordinal) { next.Id }));
                    else Add(tool.Id, next.Id, new("definition-path", null, steps));
                }
            }
        }
        if (restrictAgentAccess && root is not null && all.TryGetValue(root, out var rootNode) && rootNode.Identity.Kind is "Agent" or "AgentRevision"
            && representatives.TryGetValue(root, out var rootId) && visible.ContainsKey(rootId))
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal) { rootId }; var queue = new Queue<string>(); queue.Enqueue(rootId);
            var successors = paths.Keys.ToLookup(pair => pair.Source, pair => pair.Target);
            while (queue.TryDequeue(out var id)) foreach (var target in successors[id]) if (reachable.Add(target)) queue.Enqueue(target);
            foreach (var pair in paths.Keys.Where(pair => pair.Target == rootId)) reachable.Add(pair.Source);
            foreach (var id in visible.Keys.Where(id => !reachable.Contains(id)).ToArray()) visible.Remove(id);
            foreach (var key in paths.Keys.Where(pair => !visible.ContainsKey(pair.Source) || !visible.ContainsKey(pair.Target)).ToArray()) paths.Remove(key);
        }
        var provenance = new Dictionary<Guid, (SystemGraphNode[] Nodes, SystemGraphRelation[] Edges)>();
        (SystemGraphNode[] Nodes, SystemGraphRelation[] Edges) ToolDefinitions(SystemGraphNode tool)
        {
            if (tool.Identity.Kind != "Tool") return ([], []);
            if (provenance.TryGetValue(tool.Identity.ResourceUid, out var cached)) return cached;
            var definitions = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal); var edges = new HashSet<SystemGraphRelation>();
            var queue = new Queue<string>(observation.Nodes.Where(node => node.Identity.ResourceUid == tool.Identity.ResourceUid).Select(node => node.Id));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var id) && visited.Count < 128)
            {
                if (!visited.Add(id)) continue;
                foreach (var edge in outgoing[id])
                    if (all.TryGetValue(edge.Target, out var node) && node.Identity.Kind == "ToolDefinition")
                    { definitions[node.Id] = node; edges.Add(edge); queue.Enqueue(node.Id); }
            }
            var found = (definitions.Values.ToArray(), edges.ToArray()); provenance[tool.Identity.ResourceUid] = found; return found;
        }
        var links = paths.OrderBy(pair => pair.Key.Source, StringComparer.Ordinal).ThenBy(pair => pair.Key.Target, StringComparer.Ordinal)
            .Select(pair => new SystemExplorerLink("summary:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                pair.Key.Source + "\n" + pair.Key.Target))), visible[pair.Key.Source], visible[pair.Key.Target], pair.Value)
            { ToolDefinitions = ToolDefinitions(visible[pair.Key.Target]).Nodes, Provenance = ToolDefinitions(visible[pair.Key.Target]).Edges }).ToArray();
        var graph = window with
        {
            Nodes = visible.Values.ToArray(),
            Relations = links.Select(link => new SystemGraphRelation(link.Source.Id, link.Target.Id, link.Id)).ToArray()
        };
        return new(graph, links, represented);
    }
}
