using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphStreamTests
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static SystemGraphNode Node(string name)
    {
        var uid = Guid.NewGuid();
        return new(SystemGraphIds.Definition(uid), new(uid, Owner, ResourceNamespace.Default, "Agent", name), "definition", new(1, "etag"));
    }

    [TestMethod]
    public async Task AccessProvenanceRequiresAnAuthorizedViaEndpointAndRevocationInvalidatesTheStream()
    {
        var agent = Node("agent"); var tool = Node("tool"); var via = Node("private-toolset");
        var edge = new SystemGraphRelation(agent.Id, tool.Id, "toolset-access") { ViaNodeId = via.Id };
        var access = new Access(); var source = new Source([new([agent, tool, via], [edge])]);
        var service = new SystemGraphQueryService([source], access, TimeProvider.System);
        Assert.IsTrue((await service.QueryAsync(new(Root: agent.Id), default)).Relations.Contains(edge));
        var frames = await Collect(service, new(Root: agent.Id));
        Assert.IsTrue(frames.SelectMany(frame => frame.Relations).Contains(edge));
        access.Hidden.Add(via.Identity.ResourceUid);
        Assert.IsEmpty((await service.QueryAsync(new(Root: agent.Id), default)).Relations);
        Assert.IsFalse((await Collect(service, new(Root: agent.Id))).SelectMany(frame => frame.Relations).Contains(edge));
        access.Hidden.Clear(); access.BindingDenied.Add(via.Identity.ResourceUid);
        Assert.IsEmpty((await service.QueryAsync(new(Root: agent.Id), default)).Relations);
        Assert.IsFalse((await Collect(service, new(Root: agent.Id))).SelectMany(frame => frame.Relations).Contains(edge));
        access.BindingDenied.Clear(); source.OnRead = () => { if (source.Reads % 2 == 0) access.Hidden.Add(via.Identity.ResourceUid); };
        frames = await Collect(service, new(Root: agent.Id)); Assert.AreEqual("error", frames[^1].Kind);
    }

    [TestMethod]
    public async Task FirstNodeAndHeartbeatArriveWhileTheTailIsBlockedAndCancellationDisposesIt()
    {
        var first = Node("first"); var second = Node("second");
        var source = new Source([new([first], []), new([second], [])]) { PauseAfterFirst = true };
        var clock = new Clock(); var service = new SystemGraphQueryService([source], new Access(), clock);
        using var lifetime = new CancellationTokenSource();
        var reader = service.ReadStreamAsync(new(), lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        Assert.IsTrue(await reader.MoveNextAsync()); Assert.AreEqual("start", reader.Current.Kind);
        Assert.AreEqual(0, source.Reads, "Headers do not wait for any family enumeration.");
        Assert.IsTrue(await reader.MoveNextAsync()); Assert.AreEqual("append", reader.Current.Kind);
        Assert.AreEqual(first.Id, reader.Current.Nodes.Single().Id);
        var blocked = reader.MoveNextAsync().AsTask();
        await source.Waiting.Task;
        Assert.IsFalse(blocked.IsCompleted);
        clock.Advance();
        Assert.IsTrue(await blocked); Assert.AreEqual("heartbeat", reader.Current.Kind);
        var pending = reader.MoveNextAsync().AsTask();
        await lifetime.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);
        await reader.DisposeAsync();
        Assert.IsTrue(source.Cancelled);
    }

    [TestMethod]
    [DataRow(false, false, 0, 0)]
    [DataRow(false, true, 0, 0)]
    [DataRow(true, false, 2, 0)]
    [DataRow(true, true, 2, 1)]
    public async Task DiscoveryAndUseGateEveryBatchAndBothRelationEndpoints(bool discover, bool use, int count, int edges)
    {
        var a = Node("a"); var b = Node("b");
        var source = new Source([new([a], []), new([b], [new(a.Id, b.Id, "exact-dependency")])]);
        var frames = await Collect(new SystemGraphQueryService([source], new Access { Allowed = new(discover, discover && use) }, TimeProvider.System), new());
        Assert.AreEqual(count, frames.Sum(frame => frame.Nodes.Count));
        Assert.AreEqual(edges, frames.Sum(frame => frame.Relations.Count));
        Assert.AreEqual("complete", frames[^1].Kind);
        Assert.IsTrue(frames.SelectMany(frame => frame.Nodes).All(node => (node.Evidence is not null) == (discover && use)));
        CollectionAssert.AreEqual(Enumerable.Range(1, frames.Count).Select(index => (long)index).ToArray(), frames.Select(frame => frame.Sequence).ToArray());
        Assert.AreEqual(1, frames.Select(frame => frame.Epoch).Distinct().Count());
    }

    [TestMethod]
    public async Task HiddenMetadataAndOtherNamespacesDoNotConsumeThePublicBudgetOrAppearInFrames()
    {
        var visible = Node("visible"); var access = new Access();
        var items = new List<SystemGraphContribution> { new([visible], []) };
        for (var index = 0; index < 50; index++)
        {
            var hidden = Node("private-name"); access.Hidden.Add(hidden.Identity.ResourceUid);
            items.Add(new([hidden], [new(visible.Id, hidden.Id, "private-path")]));
        }
        var other = Node("another-namespace"); other = other with { Identity = other.Identity with { Namespace = new("other") } };
        items.Add(new([other], []));
        var frames = await Collect(new SystemGraphQueryService([new Source(items)], access, TimeProvider.System), new(ResourceNamespace.Default, MaximumNodes: 1));
        Assert.AreEqual(1, frames.Sum(frame => frame.Nodes.Count)); Assert.IsFalse(frames[^1].Truncated);
        var json = JsonSerializer.Serialize(frames);
        Assert.DoesNotContain("private-name", json, StringComparison.Ordinal); Assert.DoesNotContain("private-path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("another-namespace", json, StringComparison.Ordinal);
        Assert.IsEmpty(frames.SelectMany(frame => frame.Relations).ToArray());
    }

    [TestMethod]
    public async Task RevocationBetweenSourcePassesClearsTheObservationBeforeAnyInvisibleEdgeIsDelivered()
    {
        var a = Node("a"); var b = Node("b"); var access = new Access();
        var source = new Source([new([a], []), new([b], [new(a.Id, b.Id, "exact-dependency")])]);
        source.OnRead = () => { if (source.Reads == 2) access.Hidden.Add(a.Identity.ResourceUid); };
        var frames = await Collect(new SystemGraphQueryService([source], access, TimeProvider.System), new());
        Assert.AreEqual("error", frames[^1].Kind); Assert.AreEqual("system_graph_changed", frames[^1].ErrorCode);
        Assert.IsEmpty(frames.SelectMany(frame => frame.Relations).ToArray());
    }

    [TestMethod]
    public async Task AChangedDefinitionOrContributorFailureNeverCertifiesACompletedObservation()
    {
        foreach (var fault in new[] { "change", "exception" })
        {
            var original = Node("a"); var source = new Source([new([original], [])]);
            source.OnRead = () =>
            {
                if (source.Reads != 2) return;
                if (fault == "exception") throw new SystemGraphException("private-native-uid");
                source.Items[0] = new([original with { Evidence = new(2, "changed-etag") }], []);
            };
            var frames = await Collect(new SystemGraphQueryService([source], new Access(), TimeProvider.System), new());
            Assert.AreEqual("error", frames[^1].Kind);
            Assert.IsFalse(frames.Any(frame => frame.Kind == "complete"));
            Assert.DoesNotContain("private-native-uid", JsonSerializer.Serialize(frames), StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task NeighboursRequireAVisibleRootAndRetainOnlyItsAuthorizedDirectNeighbourhood()
    {
        var a = Node("root"); var b = Node("neighbour"); var unrelated = Node("unrelated");
        var source = new Source([new([a], []), new([b], [new(a.Id, b.Id, "exact-dependency")]), new([unrelated], [])]);
        var access = new Access(); var service = new SystemGraphQueryService([source], access, TimeProvider.System);
        var frames = await Collect(service, new(Root: a.Id));
        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, frames.SelectMany(frame => frame.Nodes).Select(node => node.Id).ToArray());
        Assert.AreEqual(1, frames.Sum(frame => frame.Relations.Count));
        access.Hidden.Add(a.Identity.ResourceUid);
        foreach (var root in new[] { a.Id, "missing" })
        {
            frames = await Collect(service, new(Root: root));
            Assert.AreEqual(0, frames.Sum(frame => frame.Nodes.Count));
            Assert.AreEqual(0, frames.Sum(frame => frame.Relations.Count));
            Assert.AreEqual("complete", frames[^1].Kind);
        }
    }

    [TestMethod]
    public async Task RootFollowsAnOutOfOrderCrossNamespaceDependencyChainWithoutPullingInOtherConsumers()
    {
        var chain = Enumerable.Range(0, 12).Select(index => Node("dependency-" + index)).ToArray();
        chain[11] = chain[11] with { Identity = chain[11].Identity with { Namespace = new("shared") } };
        var peer = Node("unrelated-agent"); var incoming = Node("root-consumer"); var other = Node("unrelated-model");
        var items = chain.Select(node => new SystemGraphContribution([node], [])).ToList();
        items.Add(new([peer, incoming, other], [new(peer.Id, chain[4].Id, "exact-dependency"),
            new(incoming.Id, chain[0].Id, "exact-dependency"), new(incoming.Id, other.Id, "exact-dependency")]));
        for (var index = 10; index >= 0; index--) items.Add(new([], [new(chain[index].Id, chain[index + 1].Id, "exact-dependency")]));
        items.Add(new([], [new(chain[8].Id, chain[2].Id, "exact-dependency")])); // A cycle must terminate.
        var access = new Access(); var service = new SystemGraphQueryService([new Source(items)], access, TimeProvider.System);
        var frames = await Collect(service, new(ResourceNamespace.Default, chain[0].Id));
        CollectionAssert.AreEquivalent(chain.Select(node => node.Id).Append(incoming.Id).ToArray(),
            frames.SelectMany(frame => frame.Nodes).Select(node => node.Id).ToArray());
        Assert.AreEqual("complete", frames[^1].Kind); Assert.IsFalse(frames[^1].Truncated);
        Assert.AreEqual(13, frames.Sum(frame => frame.Relations.Count));
        var strict = await service.QueryAsync(new(ResourceNamespace.Default, chain[0].Id), default);
        CollectionAssert.AreEquivalent(new[] { chain[0].Id, chain[1].Id, incoming.Id }, strict.Nodes.Select(node => node.Id).ToArray());
        var missing = await Collect(service, new(new ResourceNamespace("wrong"), chain[0].Id));
        Assert.IsEmpty(missing.SelectMany(frame => frame.Nodes).ToArray());
        access.Hidden.Add(chain[5].Identity.ResourceUid);
        frames = await Collect(service, new(ResourceNamespace.Default, chain[0].Id));
        Assert.IsFalse(frames.SelectMany(frame => frame.Nodes).Any(node => chain.Skip(5).Any(hidden => hidden.Id == node.Id)));
        Assert.DoesNotContain("shared", JsonSerializer.Serialize(frames), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DepthLimitsAreExplicitAndCannotBeBypassedByAnEarlierLongerPath()
    {
        var nodes = Enumerable.Range(0, 5).Select(index => Node("node-" + index)).ToArray();
        var items = nodes.Select(node => new SystemGraphContribution([node], [])).ToList();
        items.Add(new([], [new(nodes[0].Id, nodes[1].Id, "exact-dependency"), new(nodes[1].Id, nodes[2].Id, "exact-dependency"),
            new(nodes[2].Id, nodes[3].Id, "exact-dependency"), new(nodes[3].Id, nodes[4].Id, "exact-dependency")]));
        var source = new Source(items); var service = new SystemGraphQueryService([source], new Access(), TimeProvider.System);
        foreach (var depth in new[] { 0, 33 }) await Assert.ThrowsExactlyAsync<SystemGraphException>(() => Collect(service, new(MaximumDepth: depth)));
        Assert.AreEqual(0, source.Reads);
        var frames = await Collect(service, new(Root: nodes[0].Id, MaximumDepth: 2));
        Assert.AreEqual(3, frames.Sum(frame => frame.Nodes.Count)); Assert.IsTrue(frames[^1].Truncated);
        items.Add(new([], [new(nodes[0].Id, nodes[3].Id, "exact-dependency")]));
        frames = await Collect(service, new(Root: nodes[0].Id, MaximumDepth: 2));
        Assert.AreEqual(5, frames.Sum(frame => frame.Nodes.Count)); Assert.IsFalse(frames[^1].Truncated);
    }

    [TestMethod]
    public async Task DeploymentConfigurationMetadataRequiresBindingAccessToItsExactRevisionInEveryObservation()
    {
        var revision = Node("private-revision"); revision = revision with { Identity = revision.Identity with { Kind = "AgentRevision" } };
        var deployment = Node("deployment"); deployment = deployment with { Identity = deployment.Identity with { Kind = "AgentDeployment" } };
        var config = deployment with
        {
            Id = "deployment-configuration",
            State = "recorded-deployment-configuration",
            Deployment = new(deployment.Identity.ResourceUid, Guid.NewGuid(), Guid.NewGuid(), revision.Identity.ResourceUid,
                Owner, ResourceNamespace.Default, revision.Identity.Name, 1, "hash", "private-environment", "private-target-id", "Private target", "worker-pool", "awp-worker", DateTimeOffset.UnixEpoch)
        };
        var access = new Access(); var source = new Source([new([deployment, revision, config], [new(config.Id, revision.Id, "deployed-revision")])]);
        var service = new SystemGraphQueryService([source], access, TimeProvider.System);
        Assert.IsTrue((await Collect(service, new(Root: config.Id))).SelectMany(frame => frame.Nodes).Any(node => node.Id == config.Id));
        access.Hidden.Add(revision.Identity.ResourceUid);
        var frames = await Collect(service, new(Root: config.Id));
        Assert.IsEmpty(frames.SelectMany(frame => frame.Nodes).ToArray());
        Assert.DoesNotContain("private-target-id", JsonSerializer.Serialize(await service.QueryAsync(new(), default)), StringComparison.Ordinal);
        Assert.DoesNotContain("private-environment", JsonSerializer.Serialize(frames), StringComparison.Ordinal);
        access.Hidden.Clear(); source.OnRead = () => { if (source.Reads % 2 == 0) access.Hidden.Add(revision.Identity.ResourceUid); };
        frames = await Collect(service, new(Root: config.Id));
        Assert.AreEqual("error", frames[^1].Kind);
    }

    [TestMethod]
    public async Task BoundsAreExplicitAndAnInvalidLimitNeverEnumeratesFamilies()
    {
        var source = new Source([new([Node("a")], []), new([Node("b")], [])]);
        var service = new SystemGraphQueryService([source], new Access(), TimeProvider.System);
        await Assert.ThrowsExactlyAsync<SystemGraphException>(() => Collect(service, new(MaximumNodes: 0)));
        Assert.AreEqual(0, source.Reads);
        var frames = await Collect(service, new(MaximumNodes: 1));
        Assert.AreEqual(1, frames.Sum(frame => frame.Nodes.Count)); Assert.IsTrue(frames[^1].Truncated);
        Assert.AreEqual("complete", frames[^1].Kind);
    }

    private static async Task<List<SystemGraphStreamFrame>> Collect(ISystemGraphStreamService service, SystemGraphStreamQuery query)
    {
        var frames = new List<SystemGraphStreamFrame>();
        await foreach (var frame in service.ReadStreamAsync(query, default)) frames.Add(frame);
        return frames;
    }
    private sealed class Access : ISystemGraphAuthorization
    {
        public SystemGraphAccess Allowed { get; set; } = new(true, true);
        public HashSet<Guid> Hidden { get; } = [];
        public HashSet<Guid> BindingDenied { get; } = [];
        public Task<SystemGraphReadContext> GetContextAsync(CancellationToken ct) => Task.FromResult(new SystemGraphReadContext("actor", [Owner], [Owner], [new(Owner, null)]));
        public Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken ct) =>
            Task.FromResult(Hidden.Contains(identity.ResourceUid) ? new(false, false) : BindingDenied.Contains(identity.ResourceUid) ? new(true, false) : Allowed);
    }
    private sealed class Source(List<SystemGraphContribution> items) : ISystemGraphContributor
    {
        public string Family => "test";
        public List<SystemGraphContribution> Items { get; } = items;
        public int Reads { get; private set; }
        public bool PauseAfterFirst { get; init; }
        public bool Cancelled { get; private set; }
        public Action? OnRead { get; set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
        {
            Reads++; OnRead?.Invoke();
            for (var index = 0; index < Items.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (PauseAfterFirst && index == 1)
                {
                    Waiting.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    finally { Cancelled = ct.IsCancellationRequested; }
                }
                yield return Items[index];
            }
        }
    }
    private sealed class Clock : TimeProvider
    {
        private Timer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(5), dueTime);
            return timer = new(callback, state);
        }
        public void Advance() => timer!.Fire();
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
