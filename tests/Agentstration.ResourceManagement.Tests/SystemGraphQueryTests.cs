using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphQueryTests
{
    private static readonly Guid FirstUid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondUid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task RebuildOrderingAndSemanticIdentitiesSurviveRestart()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(), default);
        fixture.Contributor.Items = [.. fixture.Contributor.Items.AsEnumerable().Reverse()];
        var second = await fixture.Service.QueryAsync(new(), default);
        var restarted = await fixture.Restart().QueryAsync(new(), default);
        Assert.AreEqual(first.Snapshot, second.Snapshot);
        Assert.AreEqual(first.Snapshot, restarted.Snapshot);
        Assert.HasCount(2, first.Nodes);
        Assert.HasCount(1, first.Relations);
        Assert.IsFalse(first.GloballyComplete);
    }

    [TestMethod]
    [DataRow(false, false, 0, false)]
    [DataRow(false, true, 0, false)]
    [DataRow(true, false, 2, false)]
    [DataRow(true, true, 2, true)]
    public async Task DiscoveryAndBindingInspectionAreIndependent(bool discover, bool use, int count, bool hasRelations)
    {
        var fixture = new Fixture();
        fixture.Access.Access = new(discover, discover && use);
        var page = await fixture.Service.QueryAsync(new(), default);
        Assert.HasCount(count, page.Nodes);
        Assert.AreEqual(hasRelations ? 1 : 0, page.Relations.Count);
        Assert.IsTrue(page.Nodes.All(node => (node.Evidence is not null) == (discover && use)));
    }

    [TestMethod]
    public async Task HiddenNodesDoNotAffectSnapshotsCountsCursorsOrNeighbourResults()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(Limit: 1), default);
        var hidden = Node(Guid.NewGuid(), "invisible-name");
        fixture.Access.Hidden.Add(hidden.Identity.ResourceUid);
        fixture.Contributor.Items.Add(new([hidden], [new(fixture.First.Id, hidden.Id, "exact-dependency", "hidden-path")]));
        var second = await fixture.Service.QueryAsync(new(Limit: 1), default);
        Assert.AreEqual(first.Snapshot, second.Snapshot);
        var continuation = await fixture.Service.QueryAsync(new(Limit: 1, Cursor: first.NextCursor), default);
        Assert.HasCount(1, continuation.Nodes);
        Assert.IsFalse(continuation.HasMore);
        var invisible = await fixture.Service.QueryAsync(new(Root: hidden.Id), default);
        var missing = await fixture.Service.QueryAsync(new(Root: "missing"), default);
        Assert.AreEqual(invisible.Snapshot, missing.Snapshot);
        Assert.IsEmpty(invisible.Nodes);
        Assert.DoesNotContain("invisible-name", JsonSerializer.Serialize(second), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RevocationReauthorizesExistingCursorAndRemovesHiddenEndpoints()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(Limit: 1), default);
        fixture.Access.Hidden.Add(SecondUid);
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Service.QueryAsync(new(Limit: 1, Cursor: first.NextCursor), default));
        var current = await fixture.Service.QueryAsync(new(), default);
        Assert.HasCount(1, current.Nodes);
        Assert.IsEmpty(current.Relations);
        Assert.DoesNotContain(SecondUid.ToString(), JsonSerializer.Serialize(current), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task NamespaceAndPublicationKeysKeepHomonymsAndRecreatedUidsSeparate()
    {
        var fixture = new Fixture();
        var replacement = Node(Guid.NewGuid(), fixture.First.Identity.Name);
        fixture.Contributor.Items.Add(new([replacement], []));
        fixture.Contributor.Items.Add(new([fixture.First with { Id = SystemGraphIds.Publication(FirstUid, "1.0"), State = "publication" },
            fixture.First with { Id = SystemGraphIds.Publication(FirstUid, "2.0"), State = "publication" }], []));
        var foreignNamespace = Node(Guid.NewGuid(), fixture.First.Identity.Name) with { Identity = fixture.First.Identity with { ResourceUid = Guid.NewGuid(), Namespace = new("other") } };
        fixture.Contributor.Items.Add(new([foreignNamespace], []));
        var page = await fixture.Service.QueryAsync(new(Namespace: ResourceNamespace.Default), default);
        Assert.HasCount(5, page.Nodes);
        Assert.HasCount(5, page.Nodes.Select(node => node.Id).Distinct().ToArray());
    }

    [TestMethod]
    public async Task DraftGenerationAndImmutablePublicationEvidenceRemainDistinct()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(), default);
        fixture.Contributor.Items[0] = new([fixture.First with { Evidence = new(2, "etag-2") }], []);
        var changed = await fixture.Service.QueryAsync(new(), default);
        Assert.AreNotEqual(first.Snapshot, changed.Snapshot);
        Assert.AreEqual(first.Nodes[0].Id, changed.Nodes[0].Id);
        Assert.AreNotEqual(SystemGraphIds.Generation(FirstUid, 1), SystemGraphIds.Generation(FirstUid, 2));
    }

    [TestMethod]
    public async Task IncompleteContributorNeverReturnsAPartialAuthoritativeGraphOrItsException()
    {
        var fixture = new Fixture();
        fixture.Contributor.Failure = new InvalidOperationException("secret-scope/hidden-path");
        var exception = await Assert.ThrowsExactlyAsync<SystemGraphException>(() => fixture.Service.QueryAsync(new(), default));
        Assert.AreEqual("system_graph_unavailable", exception.Code);
        Assert.DoesNotContain("hidden-path", exception.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ConcurrentChangesAndAuthorizationChangesRejectMixedSnapshots()
    {
        var fixture = new Fixture();
        fixture.Contributor.OnRead = () =>
        {
            if (fixture.Contributor.Reads == 2)
                fixture.Contributor.Items[0] = new([fixture.First with { Evidence = new(99) }], []);
        };
        await AssertCodeAsync("system_graph_changed", () => fixture.Service.QueryAsync(new(), default));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(201)]
    public async Task InvalidPageLimitsFailBeforeReadingContributors(int limit)
    {
        var fixture = new Fixture();
        await AssertCodeAsync("system_graph_query_invalid", () => fixture.Service.QueryAsync(new(Limit: limit), default));
        Assert.AreEqual(0, fixture.Contributor.Reads);
    }

    [TestMethod]
    public async Task CancellationPropagatesWithoutReturningPartialResults()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.QueryAsync(new(), cancellation.Token));
    }

    [TestMethod]
    public async Task TamperedExpiredChangedQueryAndRestartedCursorsFailUniformly()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(Limit: 1), default);
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Service.QueryAsync(new(Limit: 1, Cursor: first.NextCursor + "x"), default));
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Service.QueryAsync(new(Limit: 2, Cursor: first.NextCursor), default));
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Restart().QueryAsync(new(Limit: 1, Cursor: first.NextCursor), default));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(6);
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Service.QueryAsync(new(Limit: 1, Cursor: first.NextCursor), default));
    }

    [TestMethod]
    public async Task VisibleNodeBudgetFailsExplicitlyInsteadOfClaimingNoDependencies()
    {
        var fixture = new Fixture();
        fixture.Contributor.Items = Enumerable.Range(0, 10001).Select(index => new SystemGraphContribution([Node(Guid.NewGuid(), index.ToString(System.Globalization.CultureInfo.InvariantCulture))], [])).ToList();
        await AssertCodeAsync("system_graph_limit_exceeded", () => fixture.Service.QueryAsync(new(), default));
    }

    [TestMethod]
    public async Task NeighboursAreBoundedToVisibleDirectRelationsAndCurrentAuthorizationContext()
    {
        var fixture = new Fixture();
        var page = await fixture.Service.QueryAsync(new(Root: fixture.First.Id), default);
        Assert.HasCount(2, page.Nodes);
        Assert.HasCount(1, page.Relations);
        var first = await fixture.Service.QueryAsync(new(Limit: 1), default);
        fixture.Access.Key = "another-principal/instance";
        await AssertCodeAsync("system_graph_cursor_invalid", () => fixture.Service.QueryAsync(new(Limit: 1, Cursor: first.NextCursor), default));
    }

    [TestMethod]
    public async Task AuthorizedRelationBudgetRejectsExcessiveEdges()
    {
        var fixture = new Fixture();
        fixture.Contributor.Items[1] = fixture.Contributor.Items[1] with
        {
            Relations = Enumerable.Range(0, 20001).Select(index => new SystemGraphRelation(fixture.First.Id,
                SystemGraphIds.Definition(SecondUid), "exact-dependency", index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()
        };
        await AssertCodeAsync("system_graph_limit_exceeded", () => fixture.Service.QueryAsync(new(), default));
    }

    [TestMethod]
    public async Task DiscoverOnlyCannotInferRecordedGenerationsFromNodeIds()
    {
        var fixture = new Fixture();
        fixture.Access.Access = new(true, false);
        fixture.Contributor.Items.Add(new([fixture.First with
        {
            Id = SystemGraphIds.Generation(FirstUid, 9876), State = "recorded-generation", Evidence = new(9876)
        }], []));
        var page = await fixture.Service.QueryAsync(new(), default);
        Assert.HasCount(2, page.Nodes);
        Assert.DoesNotContain("9876", JsonSerializer.Serialize(page), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task HiddenRelationsCannotConsumeTheAuthorizedRelationBudget()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.QueryAsync(new(), default);
        fixture.Contributor.Items.Add(new([], Enumerable.Range(0, 20001).Select(index =>
            new SystemGraphRelation(fixture.First.Id, "invisible", "exact-dependency",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()));
        var second = await fixture.Service.QueryAsync(new(), default);
        Assert.AreEqual(first.Snapshot, second.Snapshot);
        Assert.AreEqual(first.Relations.Count, second.Relations.Count);
    }

    [TestMethod]
    public async Task NeighbourPaginationRetainsTheRootAndEveryIncidentEdge()
    {
        var fixture = new Fixture();
        var third = Node(Guid.NewGuid(), "third");
        fixture.Contributor.Items.Add(new([third], [new(fixture.First.Id, third.Id, "exact-dependency")]));
        var first = await fixture.Service.QueryAsync(new(Root: fixture.First.Id, Limit: 2), default);
        var second = await fixture.Service.QueryAsync(new(Root: fixture.First.Id, Limit: 2, Cursor: first.NextCursor), default);
        Assert.IsTrue(first.HasMore);
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual(fixture.First.Id, first.Nodes[0].Id);
        Assert.AreEqual(fixture.First.Id, second.Nodes[0].Id);
        Assert.HasCount(1, first.Relations);
        Assert.HasCount(1, second.Relations);
        Assert.AreNotEqual(first.Relations[0].Target, second.Relations[0].Target);
        await AssertCodeAsync("system_graph_query_invalid", () => fixture.Service.QueryAsync(new(Root: "missing", Limit: 1), default));
    }

    [TestMethod]
    public async Task HiddenConsumerCannotRevealPinsOfAnOtherwiseVisibleDependency()
    {
        var fixture = new Fixture();
        var baseline = await fixture.Service.QueryAsync(new(), default);
        var hidden = Node(Guid.NewGuid(), "private-consumer");
        fixture.Access.Hidden.Add(hidden.Identity.ResourceUid);
        var pin = fixture.First with { Id = SystemGraphIds.Generation(fixture.First.Identity.ResourceUid, 37), State = "recorded-generation", Evidence = new(37) };
        fixture.Contributor.Items.Add(new([hidden, pin], [new(hidden.Id, pin.Id, "exact-dependency"), new(pin.Id, fixture.First.Id, "generation-of")])
        { BindingOwner = hidden.Identity, BindingOwnerNodeId = hidden.Id });
        var current = await fixture.Service.QueryAsync(new(), default);
        Assert.AreEqual(baseline.Snapshot, current.Snapshot);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Nodes), JsonSerializer.Serialize(current.Nodes));
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Relations), JsonSerializer.Serialize(current.Relations));
    }

    private static async Task AssertCodeAsync(string expected, Func<Task<SystemGraphPage>> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<SystemGraphException>(action);
        Assert.AreEqual(expected, exception.Code);
    }

    private static SystemGraphNode Node(Guid uid, string name) => new(SystemGraphIds.Definition(uid),
        new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, "ModelProfile", name), "definition", new(1, "etag"));

    private sealed class Fixture
    {
        public SystemGraphNode First { get; } = Node(FirstUid, "same-name");
        public Contributor Contributor { get; } = new();
        public Authorization Access { get; } = new();
        public Clock Clock { get; } = new();
        public SystemGraphQueryService Service { get; }
        public Fixture()
        {
            var second = Node(SecondUid, "same-name");
            Contributor.Items = [new([First], []), new([second], [new(First.Id, second.Id, "exact-dependency", "definition.modelProfile", "recorded-pin")])];
            Service = Restart();
        }
        public SystemGraphQueryService Restart() => new([Contributor], Access, Clock);
    }

    private sealed class Contributor : ISystemGraphContributor
    {
        public string Family => "fixture";
        public List<SystemGraphContribution> Items { get; set; } = [];
        public Exception? Failure { get; set; }
        public Action? OnRead { get; set; }
        public int Reads { get; private set; }
        public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            Reads++;
            OnRead?.Invoke();
            foreach (var item in Items) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class Authorization : ISystemGraphAuthorization
    {
        public HashSet<Guid> Hidden { get; } = [];
        public string Key { get; set; } = "principal/instance";
        public SystemGraphAccess Access { get; set; } = new(true, true);
        public Task<SystemGraphReadContext> GetContextAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemGraphReadContext(Key, [ResourceScopeRef.Instance]));
        public Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken cancellationToken) => Task.FromResult(Hidden.Contains(identity.ResourceUid) ? new(false, false) : Access);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
