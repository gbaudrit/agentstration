using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.ResourceManagement.Tests;

[TestClass]
public sealed class SystemGraphImpactTests
{
    [TestMethod]
    public async Task ExactHistoricalPinsProduceBoundedDeterministicPathsWithoutUnrelatedConsumers()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        var second = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.AreEqual(first.Snapshot, second.Snapshot);
        Assert.AreEqual(JsonSerializer.Serialize(first.Paths), JsonSerializer.Serialize(second.Paths));
        Assert.HasCount(5, first.Nodes);
        Assert.HasCount(4, first.Paths);
        Assert.IsFalse(first.GloballyComplete);
        Assert.IsFalse(first.Truncated);
        Assert.AreEqual("generation-drift", first.Paths[0].Assessment);
        var flow = first.Paths.Single(path => path.Nodes[^1] == "flow-v1");
        Assert.AreEqual("members[route-a].tool", flow.Relations[1].SourceField);
        Assert.AreEqual("definition.toolSets", flow.Relations[2].SourceField);
        Assert.AreEqual("graph.steps[agent].agent", flow.Relations[3].SourceField);
        Assert.IsFalse(first.Nodes.Any(node => node.Id == "unrelated"));
    }

    [TestMethod]
    public async Task HiddenIntermediateCannotExposeConsumersPathsOrCycles()
    {
        var fixture = new Fixture();
        fixture.Authorization.Hidden.Add(fixture.ToolSet.Identity.ResourceUid);
        var result = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.HasCount(2, result.Nodes);
        Assert.HasCount(1, result.Paths);
        Assert.DoesNotContain("toolset-v1", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent-v1", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.IsFalse(result.HasCycles);
        fixture.Authorization.Hidden.Add(fixture.Root.Identity.ResourceUid);
        var hidden = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        var missing = await fixture.Service.AnalyzeAsync(new("missing"), default);
        Assert.IsEmpty(hidden.Nodes);
        Assert.IsEmpty(hidden.Paths);
        Assert.AreEqual(hidden.Snapshot, missing.Snapshot);
    }

    [TestMethod]
    public async Task CyclesAndPathAndDepthBoundsNeverBecomeACompleteAnalysis()
    {
        var fixture = new Fixture();
        fixture.Contributor.Edges.Add(new(fixture.Root.Id, "flow-v1", "exact-dependency"));
        var cycle = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.IsTrue(cycle.HasCycles);
        Assert.IsFalse(cycle.GloballyComplete);
        var bounded = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id, MaximumDepth: 1), default);
        Assert.IsTrue(bounded.Truncated);
        Assert.HasCount(1, bounded.Paths);
        bounded = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id, MaximumPaths: 2), default);
        Assert.IsTrue(bounded.Truncated);
        Assert.HasCount(2, bounded.Paths);
    }

    [TestMethod]
    public async Task CompatibleDynamicSelectionNeverClaimsRepublishingDescendantsIsRequired()
    {
        var fixture = new Fixture();
        fixture.Contributor.Edges[1] = fixture.Contributor.Edges[1] with
        { Kind = "dynamic-dependency", Resolution = "latest-published-at", Assessment = "compatible-dynamic-no-republication" };
        var result = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.AreEqual("compatible-dynamic-no-republication", result.Paths.Single(path => path.Nodes[^1] == "flow-v1").Assessment);
        fixture.Contributor.Edges[1] = fixture.Contributor.Edges[1] with { Assessment = "contract-mismatch" };
        result = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.AreEqual("contract-mismatch", result.Paths.Single(path => path.Nodes[^1] == "flow-v1").Assessment);
    }

    [TestMethod]
    public async Task LogicalOperationalAndValueReferencesDoNotEnterPublicationClosure()
    {
        var fixture = new Fixture();
        foreach (var kind in new[] { "declared-reference", "operational-provenance", "value-reference", "system-membership" })
            fixture.Contributor.Edges.Add(new("unrelated", fixture.Root.Id, kind));
        var result = await fixture.Service.AnalyzeAsync(new(fixture.Root.Id), default);
        Assert.IsFalse(result.Nodes.Any(node => node.Id == "unrelated"));
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(33, 1)]
    [DataRow(1, 0)]
    [DataRow(1, 1001)]
    public async Task InvalidBoundsFailBeforeAnyRead(int depth, int paths)
    {
        var fixture = new Fixture();
        var error = await Assert.ThrowsExactlyAsync<SystemGraphException>(() => fixture.Service.AnalyzeAsync(new(fixture.Root.Id, MaximumDepth: depth, MaximumPaths: paths), default));
        Assert.AreEqual("system_graph_query_invalid", error.Code);
        Assert.AreEqual(0, fixture.Contributor.Reads);
    }

    [TestMethod]
    public async Task ChangeComparisonSharesOneAuthorizedObservationAndNeverGuessesCompatibility()
    {
        var fixture = new Fixture();
        var previous = fixture.ToolSet with { Id = "old-publication", State = "publication", Evidence = new(Publication: "9.0.0", Hash: "old", OriginKind: "User") { ContractHash = "contract" } };
        var current = previous with { Id = "new-publication", Evidence = previous.Evidence! with { Publication = "1.0.0", Hash = "new" } };
        previous = previous with { Identity = previous.Identity with { Kind = "DataSourceQuery" } };
        current = current with { Identity = previous.Identity };
        fixture.Contributor.Nodes = [previous, current];
        fixture.Contributor.Edges.Clear();
        var result = await fixture.Service.CompareAsync(new(previous.Id, current.Id), default);
        Assert.AreEqual("compatible-query-contract", result.Assessment);
        Assert.AreEqual(result.Previous.Snapshot, result.Current.Snapshot);
        Assert.AreEqual(result.Previous.ObservedAt, result.Current.ObservedAt);
        current = current with { Evidence = current.Evidence! with { ContractHash = "different-contract" } };
        fixture.Contributor.Nodes = [previous, current];
        result = await fixture.Service.CompareAsync(new(previous.Id, current.Id), default);
        Assert.AreEqual("query-contract-changed", result.Assessment);
        fixture.Authorization.Hidden.Add(current.Identity.ResourceUid);
        var unavailable = await fixture.Service.CompareAsync(new(previous.Id, current.Id), default);
        var missing = await fixture.Service.CompareAsync(new("unknown-a", "unknown-b"), default);
        Assert.AreEqual("unavailable", unavailable.Assessment);
        Assert.IsEmpty(unavailable.Current.Nodes);
        Assert.AreEqual(JsonSerializer.Serialize(missing), JsonSerializer.Serialize(unavailable));
    }

    private sealed class Fixture
    {
        public SystemGraphNode Root { get; } = Node("tool", "Tool", 2);
        public SystemGraphNode ToolSet { get; } = Node("toolset-v1", "ToolSet");
        public Authorization Authorization { get; } = new();
        public Contributor Contributor { get; } = new();
        public SystemGraphQueryService Service { get; }
        public Fixture()
        {
            var pin = new SystemGraphNode("tool-gen1", Root.Identity, "recorded-generation", new(1));
            Contributor.Nodes = [Root, pin, ToolSet, Node("agent-v1", "AgentRevision"), Node("flow-v1", "Flow"), Node("unrelated", "Flow")];
            Contributor.Edges = [new(pin.Id, Root.Id, "generation-of"), new(ToolSet.Id, pin.Id, "exact-dependency", "members[route-a].tool"),
                new("agent-v1", ToolSet.Id, "exact-dependency", "definition.toolSets"), new("flow-v1", "agent-v1", "exact-dependency", "graph.steps[agent].agent")];
            Service = new([Contributor], Authorization, new FixedClock());
        }
    }
    private static SystemGraphNode Node(string id, string kind, long generation = 1) => new(id,
        new(Guid.NewGuid(), ResourceScopeRef.Instance, ResourceNamespace.Default, kind, id), "definition", new(generation));
    private sealed class Contributor : ISystemGraphContributor
    {
        public string Family => "impact-fixture";
        public SystemGraphNode[] Nodes { get; set; } = [];
        public List<SystemGraphRelation> Edges { get; set; } = [];
        public int Reads { get; private set; }
        public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Reads++;
            yield return new(Nodes, Edges.ToArray());
        }
    }
    private sealed class Authorization : ISystemGraphAuthorization
    {
        public HashSet<Guid> Hidden { get; } = [];
        public Task<SystemGraphReadContext> GetContextAsync(CancellationToken ct) => Task.FromResult(new SystemGraphReadContext("actor", [ResourceScopeRef.Instance]));
        public Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken ct) => Task.FromResult(new SystemGraphAccess(!Hidden.Contains(identity.ResourceUid), true));
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
