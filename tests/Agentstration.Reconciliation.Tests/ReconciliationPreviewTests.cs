using Agentstration.Reconciliation.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Reconciliation.Tests;

[TestClass]
public sealed class ReconciliationPreviewTests
{
    [TestMethod]
    public async Task SelectedClosureOrdersDependenciesAndKeepsOldPinsCurrentAuthoringAndTargetChoicesDistinct()
    {
        var fixture = new Fixture(); fixture.Ports.AddShortcutPath = true;
        var result = await fixture.Service.PreviewAsync(fixture.Request, default);
        CollectionAssert.AreEqual(new[] { "set-v1", "agent-v1", "flow-v1" }, result.Steps.Select(step => step.Consumer).ToArray());
        Assert.AreEqual("2.0.0", result.Steps[0].ProposedVersion);
        Assert.AreEqual("adopt", result.Steps[1].Action);
        Assert.AreEqual("agent-adoption-policy-unavailable", result.Steps[1].Reason);
        Assert.AreEqual("conditional-family-command-unavailable", result.Steps[2].Reason);
        Assert.IsFalse(result.CanExecute);
        Assert.IsFalse(result.GloballyComplete);
        Assert.IsTrue(result.ReadSet.Any(node => node.Id == fixture.SetDefinition.Id && node.Evidence!.ETag == "\"set-draft\""));
        Assert.IsTrue(result.ReadSet.Any(node => node.Id == "old-generation" && node.Evidence!.Generation == 1));
        Assert.IsTrue(result.ReadSet.Any(node => node.Id == "current" && node.Evidence!.Generation == 2));
        Assert.IsFalse(result.ReadSet.Any(node => node.Id == "unrelated"));
        Assert.AreEqual("definition.toolSets", result.Steps[1].BeforeBindings.Single().SourceField);
    }

    [TestMethod]
    public async Task RelevantReadSetAndExplicitChoicesRejectStaleReviewButUnrelatedChangesDoNot()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.PreviewAsync(fixture.Request, default);
        fixture.Ports.Snapshot = "unrelated-update";
        fixture.Ports.Unrelated = fixture.Ports.Unrelated with { Evidence = new(999, "\"unrelated\"") };
        var second = await fixture.Service.RevalidateAsync(new(fixture.Request, first.Digest), default);
        Assert.AreEqual(first.Digest, second.Digest);
        fixture.Ports.Definitions[fixture.SetDefinition.Id] = fixture.SetDefinition with { Evidence = new(3, "\"changed\"") };
        var error = await Assert.ThrowsExactlyAsync<ReconciliationPreviewException>(() => fixture.Service.RevalidateAsync(new(fixture.Request, first.Digest), default));
        Assert.AreEqual("reconciliation_review_stale", error.Code);
        var changedChoices = fixture.Request with { TargetVersions = new Dictionary<string, string> { ["set-v1"] = "3.0.0" } };
        Assert.AreNotEqual(first.Digest, (await fixture.Service.PreviewAsync(changedChoices, default)).Digest);
        fixture.Ports.AuthorizationKey = "another-principal";
        Assert.AreNotEqual(first.Digest, (await fixture.Service.PreviewAsync(fixture.Request, default)).Digest);
    }

    [TestMethod]
    public async Task UnknownOrRevokedRootsExposeNoInventedConsumerEvidence()
    {
        var fixture = new Fixture(); fixture.Ports.Available = false;
        var result = await fixture.Service.PreviewAsync(fixture.Request, default);
        Assert.IsEmpty(result.ReadSet); Assert.IsEmpty(result.Steps); Assert.IsEmpty(result.Relations);
        Assert.Contains("source-unavailable", result.Blockers);
        Assert.Contains("selection-unavailable", result.Blockers);
        Assert.IsFalse(result.CanExecute);
    }

    [TestMethod]
    [DataRow("compatible-dynamic-no-republication", "no-op")]
    [DataRow("contract-mismatch", "blocked")]
    public async Task DynamicQueryAssessmentPropagatesWithoutChoosingAnOlderPublication(string assessment, string status)
    {
        var fixture = new Fixture(); fixture.Ports.Assessment = assessment;
        var result = await fixture.Service.PreviewAsync(fixture.Request, default);
        Assert.IsTrue(result.Steps.All(step => step.Status == status));
        Assert.IsTrue(result.Steps.All(step => step.Reason == assessment));
        Assert.IsFalse(result.CanExecute);
    }

    [TestMethod]
    public async Task ConcurrentObservationsAndCyclesOrTruncationCannotBecomeExecutable()
    {
        var fixture = new Fixture(); fixture.Ports.ChangedDuringAuthoringRead = true;
        var error = await Assert.ThrowsExactlyAsync<ReconciliationPreviewException>(() => fixture.Service.PreviewAsync(fixture.Request, default));
        Assert.AreEqual("reconciliation_observation_changed", error.Code);
        fixture.Ports.ChangedDuringAuthoringRead = false; fixture.Ports.HasCycles = true; fixture.Ports.Truncated = true;
        var result = await fixture.Service.PreviewAsync(fixture.Request, default);
        Assert.Contains("dependency-cycle", result.Blockers); Assert.Contains("analysis-truncated", result.Blockers); Assert.IsFalse(result.CanExecute);
    }

    [TestMethod]
    public async Task InvalidBoundsFailBeforeAnyMetadataRead()
    {
        var fixture = new Fixture();
        foreach (var request in new[] { fixture.Request with { Consumers = [] }, fixture.Request with { Consumers = Enumerable.Repeat("consumer", 33).ToArray() },
            fixture.Request with { TargetVersions = new Dictionary<string, string> { ["not-selected"] = "2.0" } } })
            await Assert.ThrowsExactlyAsync<ReconciliationPreviewException>(() => fixture.Service.PreviewAsync(request, default));
        Assert.AreEqual(0, fixture.Ports.Reads);
    }

    private sealed class Fixture
    {
        public Ports Ports { get; } = new();
        public SystemGraphNode SetDefinition => Ports.Definitions.Values.Single(node => node.Identity.Kind == "ToolSet");
        public ReconciliationPreviewService Service { get; }
        public ReconciliationPreviewRequest Request { get; } = new("old-generation", "current", ["flow-v1", "agent-v1", "set-v1"],
            TargetVersions: new Dictionary<string, string> { ["set-v1"] = "2.0.0", ["flow-v1"] = "2.0" });
        public Fixture() => Service = new(Ports, Ports, Ports);
    }
    private sealed class Ports : ISystemGraphImpactService, ISystemGraphQueryService, ISystemGraphAuthorization
    {
        private readonly ResourceScopeRef owner = ResourceScopeRef.Workspace(Guid.NewGuid());
        private readonly SystemGraphNode root, previous, set, agent, flow;
        public Dictionary<string, SystemGraphNode> Definitions { get; } = new(StringComparer.Ordinal);
        public SystemGraphNode Unrelated { get; set; }
        public string Snapshot { get; set; } = "observation";
        public string AuthorizationKey { get; set; } = "principal";
        public string Assessment { get; set; } = "recorded-dependency-review";
        public bool Available { get; set; } = true;
        public bool AddShortcutPath { get; set; }
        public bool ChangedDuringAuthoringRead { get; set; }
        private bool authoringReadOccurred;
        public bool HasCycles { get; set; }
        public bool Truncated { get; set; }
        public int Reads { get; private set; }
        public Ports()
        {
            var rootUid = Guid.NewGuid(); root = Node("current", rootUid, "ModelProfile", 2); previous = Node("old-generation", rootUid, "ModelProfile", 1);
            set = Node("set-v1", Guid.NewGuid(), "ToolSet", 1); agent = Node("agent-v1", Guid.NewGuid(), "AgentRevision", 1); flow = Node("flow-v1", Guid.NewGuid(), "Flow", 1);
            Definitions[SystemGraphIds.Definition(set.Identity.ResourceUid)] = Node(SystemGraphIds.Definition(set.Identity.ResourceUid), set.Identity.ResourceUid, "ToolSet", 2) with { Evidence = new(2, "\"set-draft\"") };
            var parentUid = Guid.NewGuid();
            Definitions[SystemGraphIds.Definition(parentUid)] = Node(SystemGraphIds.Definition(parentUid), parentUid, "Agent", 1);
            Definitions[SystemGraphIds.Definition(flow.Identity.ResourceUid)] = Node(SystemGraphIds.Definition(flow.Identity.ResourceUid), flow.Identity.ResourceUid, "Flow", 1);
            Unrelated = Node("unrelated", Guid.NewGuid(), "Flow", 1);
        }
        private SystemGraphNode Node(string id, Guid uid, string kind, long generation) => new(id, new(uid, owner, ResourceNamespace.Default, kind, kind), "definition", new(generation, "\"etag\""));
        public Task<SystemGraphChangeResult> CompareAsync(SystemGraphChangeQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            var edge1 = new SystemGraphRelation(set.Id, previous.Id, "exact-dependency", "members[tool].tool");
            var edge2 = new SystemGraphRelation(agent.Id, set.Id, "exact-dependency", "definition.toolSets");
            var edge3 = new SystemGraphRelation(flow.Id, agent.Id, "exact-dependency", "graph.agent");
            var all = new[] { root, previous, set, agent, flow, Unrelated };
            var before = new[] { new SystemGraphImpactPath([previous.Id, set.Id], [edge1], Assessment), new([previous.Id, set.Id, agent.Id], [edge1, edge2], Assessment), new([previous.Id, set.Id, agent.Id, flow.Id], [edge1, edge2, edge3], Assessment) };
            if (AddShortcutPath) before = [.. before, new([previous.Id, flow.Id], [new(flow.Id, previous.Id, "exact-dependency", "graph.alternate")], Assessment)];
            var generation = new SystemGraphRelation(previous.Id, root.Id, "generation-of");
            var after = before.Select(path => new SystemGraphImpactPath([root.Id, .. path.Nodes], [generation, .. path.Relations], Assessment)).ToArray();
            SystemGraphImpactResult Result(SystemGraphImpactPath[] paths) => new(Available ? all : [], Available ? paths : [], ChangedDuringAuthoringRead && authoringReadOccurred ? "changed" : Snapshot, DateTimeOffset.UnixEpoch, Truncated, HasCycles, false, ["partial"]);
            return Task.FromResult(new SystemGraphChangeResult(Available ? "generation-drift" : "unavailable", Result(before), Result(after)));
        }
        public Task<SystemGraphPage> QueryAsync(SystemGraphQuery query, CancellationToken cancellationToken)
        {
            Reads++; authoringReadOccurred = true; return Task.FromResult(new SystemGraphPage([.. Definitions.Values, agent], [new(Definitions.Values.Single(node => node.Identity.Kind == "Agent").Id, agent.Id, "publication")], ChangedDuringAuthoringRead ? "changed" : Snapshot, DateTimeOffset.UnixEpoch, "partial", false, false, null));
        }
        public Task<SystemGraphReadContext> GetContextAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemGraphReadContext(AuthorizationKey, [owner]));
        public Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken cancellationToken) => throw new AssertFailedException();
        public Task<SystemGraphImpactResult> AnalyzeAsync(SystemGraphImpactQuery query, CancellationToken cancellationToken) => throw new AssertFailedException();
        public Task<IReadOnlyList<SystemGraphContributorCoverage>> GetCoverageAsync(CancellationToken cancellationToken) => throw new AssertFailedException();
    }
}
