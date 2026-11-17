using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.SystemGraph;

public sealed partial class SystemGraphQueryService
{
    public async IAsyncEnumerable<SystemGraphStreamFrame> ReadStreamAsync(SystemGraphStreamQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaximumNodes is < 1 or > MaximumNodes || query.MaximumDepth is < 1 or > 32 || query.Root?.Length > 512)
            throw new SystemGraphException("system_graph_query_invalid");
        if (families.Length == 0 || families.Select(family => family.Family).Distinct(StringComparer.Ordinal).Count() != families.Length)
            throw new SystemGraphException("system_graph_unavailable");
        var context = await authorization.GetContextAsync(ct);
        var epoch = Guid.NewGuid();
        long sequence = 0;
        SystemGraphStreamFrame Frame(StreamBatch batch) => new(epoch, ++sequence, batch.Kind, timeProvider.GetUtcNow(),
            batch.Nodes, batch.Relations, batch.Scopes, Truncated: batch.Truncated, ErrorCode: batch.ErrorCode);
        yield return Frame(new("start"));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = ReadProgressiveAsync(query, context, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                next ??= reader.MoveNextAsync().AsTask();
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5), timeProvider, tick.Token));
                await tick.CancelAsync();
                ct.ThrowIfCancellationRequested();
                string? failure = null;
                bool available = false;
                bool ready = false;
                try
                {
                    await VerifyStreamContextAsync(context, ct);
                    ready = next.IsCompleted;
                    if (ready) available = await next;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (SystemGraphException exception)
                {
                    failure = exception.Code is "system_graph_forbidden" or "system_graph_changed" or "system_graph_limit_exceeded"
                        ? exception.Code : "system_graph_unavailable";
                }
                catch (Exception) { failure = "system_graph_unavailable"; }
                if (failure is not null)
                {
                    yield return Frame(new("error", ErrorCode: failure));
                    yield break;
                }
                if (!ready)
                {
                    // A slow family produces no identities or counts until it supplies authorized metadata.
                    yield return Frame(new("heartbeat"));
                    continue;
                }
                if (!available) yield break;
                yield return Frame(reader.Current);
                next = null;
            }
        }
        finally
        {
            // Cancel pending metadata I/O before disposing an iterator suspended in MoveNextAsync.
            await lifetime.CancelAsync();
            if (next is not null)
            {
                try { await next; }
                catch (Exception) { /* The public stream has already stopped or returned a generic error. */ }
            }
            await reader.DisposeAsync();
        }
    }

    private async Task VerifyStreamContextAsync(SystemGraphReadContext expected, CancellationToken ct)
    {
        var current = await authorization.GetContextAsync(ct);
        static string Key(SystemGraphReadContext value) => JsonSerializer.Serialize(value, JsonOptions);
        if (Key(current) != Key(expected)) throw new SystemGraphException("system_graph_changed");
    }

    private async IAsyncEnumerable<StreamBatch> ReadProgressiveAsync(SystemGraphStreamQuery query,
        SystemGraphReadContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var nodes = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        var access = new Dictionary<string, SystemGraphAccess>(StringComparer.Ordinal);
        var owners = new Dictionary<string, HashSet<SystemGraphIdentity>>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var related = new HashSet<string>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingNodes = new Dictionary<string, SystemGraphNode>(StringComparer.Ordinal);
        var edges = new Dictionary<SystemGraphRelation, HashSet<SystemGraphIdentity>>();
        var emittedEdges = new HashSet<SystemGraphRelation>();
        var pendingEdges = new HashSet<SystemGraphRelation>();
        var truncated = false;
        var firstEdges = true;

        async Task<SystemGraphAccess> Evaluate(SystemGraphIdentity identity)
        {
            using var trusted = metadataScope?.Enter();
            return await authorization.EvaluateAsync(identity, context, ct);
        }
        async Task Verify(string id)
        {
            if (await Evaluate(nodes[id].Identity) != access[id] || !await DeploymentVisibleAsync(nodes[id], context, ct)) throw new SystemGraphException("system_graph_changed");
            if (owners.TryGetValue(id, out var guards))
                foreach (var owner in guards)
                    if (await Evaluate(owner) is not { Discover: true, InspectBindings: true })
                        throw new SystemGraphException("system_graph_changed");
        }
        void QueueNode(string id)
        {
            if (!emitted.Contains(id)) pendingNodes.TryAdd(id, nodes[id]);
        }
        bool QueueEdge(SystemGraphRelation edge)
        {
            var expanded = false;
            if (query.Root is { } root)
            {
                if (!nodes.TryGetValue(root, out var rootNode) || query.Namespace is { } ns && rootNode.Identity.Namespace != ns) return false;
                related.Add(root);
                depths.TryAdd(root, 0);
                // Retain direct consumers of the root, but do not expand through them into
                // unrelated consumers of shared dependencies. Dependency traversal is outgoing.
                if (edge.Target == root) related.Add(edge.Source);
                if (depths.TryGetValue(edge.Source, out var depth) && depth < query.MaximumDepth
                    && (!depths.TryGetValue(edge.Target, out var previous) || previous > depth + 1))
                {
                    depths[edge.Target] = depth + 1;
                    related.Add(edge.Target);
                    expanded = true;
                }
                if (!related.Contains(edge.Source) || !related.Contains(edge.Target)) return expanded;
            }
            QueueNode(edge.Source); QueueNode(edge.Target);
            if (!emittedEdges.Contains(edge)) pendingEdges.Add(edge);
            return expanded;
        }
        async Task<StreamBatch> Flush()
        {
            await VerifyStreamContextAsync(context, ct);
            // Recheck everything this batch is about to disclose, even if collected earlier.
            foreach (var id in pendingNodes.Keys.Concat(pendingEdges.SelectMany(edge => new[] { edge.Source, edge.Target })).Distinct())
                await Verify(id);
            foreach (var edge in pendingEdges)
                foreach (var owner in edges[edge])
                    if (await Evaluate(owner) is not { Discover: true, InspectBindings: true })
                        throw new SystemGraphException("system_graph_changed");
            var batch = new StreamBatch("append", pendingNodes.Values.ToArray(), pendingEdges.ToArray(),
                (context.Hierarchy ?? []).Where(scope => pendingNodes.Values.Any(node => node.Identity.OwnerScope == scope.Scope)).ToArray());
            emitted.UnionWith(pendingNodes.Keys); emittedEdges.UnionWith(pendingEdges);
            pendingNodes.Clear(); pendingEdges.Clear();
            return batch;
        }

        // Send the first authorized contribution immediately, then bounded node batches.
        await foreach (var contribution in ReadSourcesAsync(context, ct))
        {
            var bindings = contribution.BindingOwner is not { } owner || await Evaluate(owner) is { Discover: true, InspectBindings: true };
            foreach (var node in contribution.Nodes)
            {
                if ((!bindings && node.Id != contribution.BindingOwnerNodeId)
                    || query.Root is null && query.Namespace is { } ns && node.Identity.Namespace != ns) continue;
                var allowed = await Evaluate(node.Identity);
                if (!allowed.Discover || (node.State.StartsWith("recorded-", StringComparison.Ordinal) || node.State == "reference-identity") && !allowed.InspectBindings
                    || !await DeploymentVisibleAsync(node, context, ct)) continue;
                var visible = allowed.InspectBindings ? node : node with { Evidence = null, Deployment = null };
                if (nodes.TryGetValue(node.Id, out var existing))
                {
                    if (existing != visible || access[node.Id] != allowed) throw new SystemGraphException("system_graph_changed");
                }
                else
                {
                    if (nodes.Count == query.MaximumNodes) { truncated = true; break; }
                    nodes.Add(node.Id, visible); access.Add(node.Id, allowed);
                }
                if (bindings && contribution.BindingOwner is { } guard)
                {
                    if (!owners.TryGetValue(node.Id, out var guards)) owners[node.Id] = guards = [];
                    guards.Add(guard);
                }
                if (query.Root is null || node.Id == query.Root && (query.Namespace is null || node.Identity.Namespace == query.Namespace)) QueueNode(node.Id);
                if (pendingNodes.Count >= 20)
                    yield return await Flush();
            }
            if (emitted.Count == 0 && pendingNodes.Count > 0) yield return await Flush();
            if (truncated) break;
        }
        if (pendingNodes.Count > 0) yield return await Flush();

        // A second source pass retains only edges with already authorized endpoints. Hidden
        // identities and unresolved edges never consume the returned relation budget.
        if (!truncated)
        {
            await foreach (var contribution in ReadSourcesAsync(context, ct))
            {
                var bindings = contribution.BindingOwner is not { } owner || await Evaluate(owner) is { Discover: true, InspectBindings: true };
                foreach (var node in contribution.Nodes)
                {
                    if (!nodes.TryGetValue(node.Id, out var previous)) continue;
                    var allowed = await Evaluate(node.Identity);
                    var visible = allowed.InspectBindings ? node : node with { Evidence = null, Deployment = null };
                    if (!allowed.Discover || previous != visible || allowed != access[node.Id] || !await DeploymentVisibleAsync(node, context, ct)) throw new SystemGraphException("system_graph_changed");
                }
                if (!bindings) continue;
                foreach (var edge in contribution.Relations)
                {
                    if (!nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target)
                        || edge.Kind != "system-membership" && (!access[edge.Source].InspectBindings || !access[edge.Target].InspectBindings)) continue;
                    if (!edges.TryGetValue(edge, out var guards))
                    {
                        if (edges.Count == MaximumRelations) { truncated = true; break; }
                        edges[edge] = guards = [];
                    }
                    if (contribution.BindingOwner is { } guard) guards.Add(guard);
                    QueueEdge(edge);
                    if (pendingNodes.Count >= 20 || pendingEdges.Count >= 100 || firstEdges && pendingEdges.Count > 0)
                    {
                        yield return await Flush(); firstEdges = false;
                    }
                }
                if (truncated) break;
            }
        }
        // Sources can supply an entire dependency chain in reverse order. Revisit the
        // authorized bounded edge set until no shorter path remains; cycles terminate.
        for (var pass = 0; pass < query.MaximumDepth; pass++)
        {
            var expanded = false;
            foreach (var edge in edges.Keys)
            {
                ct.ThrowIfCancellationRequested();
                expanded |= QueueEdge(edge);
                if (pendingNodes.Count >= 20 || pendingEdges.Count >= 100) yield return await Flush();
            }
            if (!expanded) break;
        }
        if (query.Root is not null && edges.Keys.Any(edge => depths.TryGetValue(edge.Source, out var depth)
            && depth >= query.MaximumDepth && !depths.ContainsKey(edge.Target))) truncated = true;
        if (pendingNodes.Count > 0 || pendingEdges.Count > 0) yield return await Flush();
        foreach (var id in emitted) { ct.ThrowIfCancellationRequested(); await Verify(id); }
        foreach (var edge in emittedEdges)
            foreach (var owner in edges[edge])
                if (await Evaluate(owner) is not { Discover: true, InspectBindings: true }) throw new SystemGraphException("system_graph_changed");
        await VerifyStreamContextAsync(context, ct);
        yield return new("complete", Truncated: truncated);
    }

    private sealed record StreamBatch(string Kind, IReadOnlyList<SystemGraphNode>? NodeItems = null,
        IReadOnlyList<SystemGraphRelation>? EdgeItems = null, IReadOnlyList<SystemGraphScope>? ScopeItems = null,
        bool Truncated = false, string? ErrorCode = null)
    {
        public IReadOnlyList<SystemGraphNode> Nodes => NodeItems ?? [];
        public IReadOnlyList<SystemGraphRelation> Relations => EdgeItems ?? [];
        public IReadOnlyList<SystemGraphScope> Scopes => ScopeItems ?? [];
    }
}
