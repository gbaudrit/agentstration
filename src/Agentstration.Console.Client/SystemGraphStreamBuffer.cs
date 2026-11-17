using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Web.Console;

/// <summary>One finite observation. A gap, error or incomplete body invalidates all accumulated data.</summary>
public sealed class SystemGraphStreamBuffer
{
    private readonly Dictionary<string, SystemGraphNode> nodes = new(StringComparer.Ordinal);
    private readonly List<SystemGraphNode> orderedNodes = [];
    private readonly HashSet<SystemGraphRelation> relations = [];
    private readonly Dictionary<Agentstration.Resources.ResourceScopeRef, SystemGraphScope> scopes = [];
    private Guid epoch;
    private long sequence;
    public bool Complete { get; private set; }
    public bool Truncated { get; private set; }
    public int Count => nodes.Count;
    public SystemGraphPage? Page { get; private set; }

    public bool Apply(SystemGraphStreamFrame frame)
    {
        if (frame.Nodes is null || frame.Relations is null || frame.Scopes is null) return Fail();
        if (frame.Epoch == Guid.Empty || Complete || frame.Sequence != sequence + 1
            || sequence == 0 && frame.Kind != "start" || sequence != 0 && frame.Kind == "start" || sequence != 0 && frame.Epoch != epoch)
            return Fail();
        if (frame.Kind == "error") return Fail();
        if (frame.Kind is not ("start" or "append" or "heartbeat" or "complete")) return Fail();
        if (frame.Kind != "append" && (frame.Nodes.Count != 0 || frame.Relations.Count != 0 || frame.Scopes.Count != 0)) return Fail();
        epoch = frame.Epoch; sequence = frame.Sequence;
        if (frame.Kind == "append")
        {
            foreach (var node in frame.Nodes)
            {
                if (nodes.TryGetValue(node.Id, out var previous))
                {
                    if (previous != node) return Fail();
                }
                else { nodes.Add(node.Id, node); orderedNodes.Add(node); }
            }
            foreach (var relation in frame.Relations)
            {
                if (!nodes.ContainsKey(relation.Source) || !nodes.ContainsKey(relation.Target)) return Fail();
                relations.Add(relation);
            }
            foreach (var scope in frame.Scopes) scopes[scope.Scope] = scope;
            if (nodes.Count > 10000 || relations.Count > 20000) return Fail();
        }
        Complete = frame.Kind == "complete";
        Truncated = Complete && frame.Truncated;
        if (frame.Kind != "heartbeat")
            Page = new(orderedNodes.ToArray(), relations.ToArray(), $"stream:{epoch:N}:{sequence}", frame.ObservedAt,
                Truncated ? "bounded-progressive-observation" : frame.Coverage, false, false, null)
            { Scopes = scopes.Values.ToArray() };
        return true;
    }
    public void Clear() => Fail();
    private bool Fail()
    {
        nodes.Clear(); orderedNodes.Clear(); relations.Clear(); scopes.Clear(); Page = null; Complete = false; Truncated = false;
        epoch = Guid.Empty; sequence = 0;
        return false;
    }
}
