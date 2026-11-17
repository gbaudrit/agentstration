using System.Diagnostics.Metrics;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Api;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.SystemOperations.Tests;

[TestClass]
public sealed class SystemOperationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    [TestMethod]
    public async Task ExactUidOwnerNamespaceInvocationAndPublicationEvidenceExcludeHomonymsAndDuplicates()
    {
        using var f = new Fixture();
        var own = f.Run();
        f.Rows = [own, own, own with { RunId = "foreign-owner", DefinitionOwner = ResourceScopeRef.Tenant(Guid.NewGuid()) },
            own with { RunId = "foreign-namespace", DefinitionNamespace = ResourceNamespace.Parse("other") },
            own with { RunId = "recreated", ResourceUid = Guid.NewGuid() },
            own with { RunId = "logical", InvocationScope = ResourceScopeRef.Instance },
            own with { RunId = "data", DataScope = ResourceScopeRef.Instance }];
        var snapshot = await f.Service.SnapshotAsync(new(f.Root.Id), default);
        Assert.HasCount(1, snapshot.Facts!.Runs);
        Assert.AreEqual(1, snapshot.Facts.Metrics!.ObservedSucceeded);
        Assert.AreEqual(2000d, snapshot.Facts.Metrics.MeanCompletedDurationMilliseconds);
        Assert.IsNull(snapshot.Facts.Metrics.ReportedInputTokens);
        Assert.AreEqual("unavailable", snapshot.Facts.Metrics.TokenCoverage);
        f.Root = f.Root with { State = "recorded-generation", Evidence = new(Generation: 2) };
        Assert.IsNull((await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts!.Metrics);
        f.Rows = [own with { Publication = "2" }];
        Assert.HasCount(1, (await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts!.Runs);
    }
    [TestMethod]
    public async Task NativeRevisionRequiresItsUidAndHashAndNeverRebindsByName()
    {
        using var f = new Fixture();
        f.Root = f.Root with { Identity = f.Root.Identity with { Kind = "AgentRevision" }, State = "publication", Evidence = new(Hash: "immutable") };
        f.Rows = [f.Run(), f.Run() with { RunId = "right", RevisionUid = f.Root.Identity.ResourceUid, DefinitionHash = "immutable" },
            f.Run() with { RunId = "wrong-hash", RevisionUid = f.Root.Identity.ResourceUid, DefinitionHash = "other" }];
        CollectionAssert.AreEqual(new[] { "right" }, (await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts!.Runs.Select(run => run.RunId).ToArray());
    }
    [TestMethod]
    public async Task MissingRunsAreUnknownAndLackOfReadRightsNeverQueriesFamilyStores()
    {
        using var f = new Fixture();
        f.Context = f.Context with { CanReadRuns = false };
        var result = await f.Service.SnapshotAsync(new(f.Root.Id), default);
        Assert.IsNull(result.Facts!.Metrics); Assert.AreEqual(0, f.Reads);
        Assert.AreEqual("run-read-required", result.Coverage);
        f.Available = false;
        Assert.IsNull((await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts);
    }
    [TestMethod]
    public async Task DeliveryRejectsRevocationAndRelevantTopologyChangesDuringCollection()
    {
        using var f = new Fixture();
        f.Deliver = false;
        await Assert.ThrowsExactlyAsync<SystemOperationsException>(() => f.Service.SnapshotAsync(new(f.Root.Id), default));
        f.Deliver = true; f.Reads = 0; f.MutateGraph = true;
        await Assert.ThrowsExactlyAsync<SystemOperationsException>(() => f.Service.SnapshotAsync(new(f.Root.Id), default));
    }
    [TestMethod]
    public async Task QueryBoundsAreCheckedBeforeAnyAuthorizationOrStorageRead()
    {
        using var f = new Fixture();
        foreach (var query in new[] { new SystemOperationsQuery(""), new(f.Root.Id, WindowMinutes: 0), new(f.Root.Id, MaximumRuns: 101) })
            await Assert.ThrowsExactlyAsync<SystemOperationsException>(() => f.Service.SnapshotAsync(query, default));
        Assert.AreEqual(0, f.AccessReads);
    }
    [TestMethod]
    public async Task OrderedStreamAndClientBufferDeduplicateRejectGapsAndRequireNewSnapshot()
    {
        using var f = new Fixture();
        f.Rows = [f.Run()];
        var snapshot = await f.Service.SnapshotAsync(new(f.Root.Id), default);
        var stream = new SystemOperationsStreamState(); var buffer = new SystemOperationsBuffer();
        var first = stream.Next(snapshot); Assert.AreEqual("snapshot", first.Kind); Assert.IsTrue(buffer.Apply(first, f.Root.Id));
        Assert.IsTrue(buffer.Apply(first, f.Root.Id));
        var heartbeat = stream.Next(snapshot with { SampledAt = Now.AddSeconds(5) });
        Assert.AreEqual("heartbeat", heartbeat.Kind); Assert.IsTrue(buffer.Apply(heartbeat, f.Root.Id));
        Assert.IsFalse(buffer.Apply(heartbeat with { Sequence = 4, BaseSequence = 3 }, f.Root.Id)); Assert.IsNull(buffer.Facts);
        Assert.IsFalse(buffer.Apply(heartbeat, f.Root.Id));
        var next = new SystemOperationsStreamState().Next(snapshot);
        Assert.IsTrue(buffer.Apply(next, f.Root.Id));
        Assert.IsFalse(buffer.Apply(new SystemOperationsStreamState().Next(snapshot), f.Root.Id)); Assert.IsNull(buffer.Facts);
        Assert.IsTrue(buffer.Apply(new SystemOperationsStreamState().Next(snapshot), f.Root.Id));
        Assert.IsFalse(buffer.Apply(stream.Stop(Now, "revoked"), f.Root.Id)); Assert.IsNull(buffer.Facts);
    }
    [TestMethod]
    public void SubscriptionQuotaIsBoundedPerActorAndGloballyAndReleaseIsIdempotent()
    {
        var quotas = new SystemOperationsSubscriptions(); var actor = Guid.NewGuid();
        var leases = Enumerable.Range(0, 8).Select(_ => quotas.TryAcquire(actor)!).ToList();
        Assert.IsNull(quotas.TryAcquire(actor));
        leases[0].Dispose(); leases[0].Dispose();
        leases.Add(quotas.TryAcquire(actor)!);
        for (var index = 0; index < 7; index++)
        {
            var other = Guid.NewGuid();
            leases.AddRange(Enumerable.Range(0, 8).Select(_ => quotas.TryAcquire(other)!));
        }
        Assert.IsNull(quotas.TryAcquire(Guid.NewGuid()));
        foreach (var lease in leases) lease.Dispose();
        Assert.IsNotNull(quotas.TryAcquire(actor));
    }
    [TestMethod]
    public void MetricDimensionsCannotContainResourceRunWorkerOrUserIdentifiers()
    {
        var observed = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) => { if (instrument.Meter.Name == SystemOperationsTelemetry.Name) meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => { foreach (var tag in tags) observed.Add(tag); });
        listener.Start();
        using var telemetry = new SystemOperationsTelemetry();
        telemetry.Record("secret-resource-uid", "secret-run-id");
        Assert.HasCount(2, observed);
        CollectionAssert.AreEquivalent(new[] { "resource.family", "evidence.coverage" }, observed.Select(tag => tag.Key).ToArray());
        CollectionAssert.AreEquivalent(new[] { "other", "unknown" }, observed.Select(tag => (string)tag.Value!).ToArray());
    }
    [TestMethod]
    public async Task DurableStateTransitionsKeepAttemptsPresenceAndPublicationSeparateAndPreservePartialTokens()
    {
        using var f = new Fixture();
        f.Context = f.Context with { CanInspectWorkers = true };
        var canonical = f.Root;
        var attempt = new SystemWorkerEvidence(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "active", Now, Now, Now.AddSeconds(10), null, "online");
        var placement = new SystemPlacementEvidence(Guid.NewGuid(), "assigned", [attempt, attempt with { AttemptId = Guid.NewGuid(), FencingGeneration = 2 }]);
        f.Rows = [f.Run() with { State = "running", CompletedAt = null, InputTokens = 2, OutputTokens = 3, TokenCoverage = "partial", Placements = [placement] }];
        var first = (await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts!;
        Assert.AreEqual(1, first.Metrics!.ObservedActive); Assert.AreEqual("online", first.WorkerPresence);
        Assert.AreEqual("reported-partial", first.Metrics.TokenCoverage);
        foreach (var presence in new[] { "stale", "offline" })
        {
            f.Time.Current = f.Time.Current.AddMinutes(1);
            f.Rows = [f.Rows[0] with { State = "failed", CompletedAt = Now, Placements = [placement with { Attempts = [attempt with { Presence = presence, AttemptState = "interrupted" }] }] }];
            var result = (await f.Service.SnapshotAsync(new(f.Root.Id), default)).Facts!;
            Assert.AreEqual(1, result.Metrics!.ObservedFailed); Assert.AreEqual(0, result.Metrics.ObservedActive);
            Assert.AreEqual(presence, result.WorkerPresence);
            Assert.AreEqual(canonical, f.Root); Assert.AreEqual("definition", result.PublicationState);
        }
    }
    private sealed class Fixture : ISystemGraphQueryService, ISystemOperationsAccess, ISystemOperationsEvidenceReader, IDisposable
    {
        private readonly SystemOperationsTelemetry telemetry = new();
        public SystemOperationsService Service { get; }
        public Clock Time { get; } = new();
        public SystemGraphNode Root = new("root", new(Guid.NewGuid(), ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent", "homonym"), "definition");
        public SystemOperationsReadContext Context = new(Guid.NewGuid(), ResourceScopeRef.Workspace(Guid.NewGuid()), ResourceScopeRef.Workspace(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), true, false, false, "current");
        public IReadOnlyList<SystemRunEvidence> Rows = [];
        public bool Deliver = true, MutateGraph, Available = true;
        public int Reads, AccessReads;
        public Fixture() => Service = new(this, this, this, Time, telemetry);
        public SystemRunEvidence Run() => new("runtime", "run", Context.InvocationScope, Context.DataScope, Root.Identity.OwnerScope, Root.Identity.Namespace,
            Root.Identity.ResourceUid, null, "1", "hash", "succeeded", Now.AddSeconds(-10), Now.AddSeconds(-3), Now.AddSeconds(-1), null, null, "unavailable", []);
        public Task<SystemGraphPage> QueryAsync(SystemGraphQuery query, CancellationToken ct) =>
            Task.FromResult(new SystemGraphPage(Available ? [Root] : [], [], MutateGraph && Reads > 0 ? "changed" : "stable", Now, "bounded", false, false, null));
        public Task<SystemOperationsReadContext> GetAsync(CancellationToken ct) { ++AccessReads; return Task.FromResult(Context); }
        public Task<bool> CanDeliverAsync(SystemGraphIdentity identity, SystemOperationsReadContext expected, bool inspectBindings, CancellationToken ct) => Task.FromResult(Deliver);
        public Task<SystemRunEvidencePage> ReadAsync(SystemGraphNode root, SystemOperationsReadContext context, DateTimeOffset start, int take, CancellationToken ct)
        { ++Reads; return Task.FromResult(new SystemRunEvidencePage(Rows, false, "attested-window")); }
        public Task<long?> CurrentGenerationAsync(SystemGraphIdentity identity, CancellationToken ct) => Task.FromResult<long?>(2);
        public Task<string> ProvisioningAsync(SystemGraphNode root, CancellationToken ct) => Task.FromResult("unknown");
        public void Dispose() => telemetry.Dispose();
    }
    public sealed class Clock : TimeProvider { public DateTimeOffset Current { get; set; } = Now; public override DateTimeOffset GetUtcNow() => Current; }
}
