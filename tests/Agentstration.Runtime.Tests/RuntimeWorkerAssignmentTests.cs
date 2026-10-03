using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeWorkerAssignmentTests
{
    private static readonly WorkspaceId Workspace = new(Guid.Parse("9bc73ec9-83b2-44ef-96e4-971d2d2eb85e"));

    [TestMethod]
    public async Task ConcurrentWorkersCannotOwnTheSameAssignment()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();

        var claims = await Task.WhenAll(
            fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid()),
            fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.AreEqual(1, claims.Count(value => value is not null));
        var stored = await fixture.Assignments.GetAsync(Workspace, fixture.AssignmentId, default);
        Assert.IsNotNull(stored);
        Assert.AreEqual(RuntimeAssignmentState.Assigned, stored.Value.State);
        Assert.AreEqual(1, stored.Value.Attempts.Count);
    }

    [TestMethod]
    public async Task HeartbeatUsesServerTimeAndRejectsAnotherOpaqueToken()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        var originalExpiry = claim.Assignment.CurrentAttempt!.LeaseExpiresAt;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));

        var heartbeat = await fixture.Service.HeartbeatAsync(claim.Ownership, default);

        Assert.AreEqual(fixture.Clock.GetUtcNow().AddSeconds(45), heartbeat.Assignment.CurrentAttempt!.LeaseExpiresAt);
        Assert.IsTrue(heartbeat.Assignment.CurrentAttempt.LeaseExpiresAt > originalExpiry);
        var forged = claim.Ownership with { OwnershipToken = "another-opaque-token" };
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.HeartbeatAsync(forged, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, exception.Code);
        var staleFence = claim.Ownership with { FencingGeneration = claim.Ownership.FencingGeneration + 1 };
        exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.HeartbeatAsync(staleFence, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.FencingRejected, exception.Code);
        var anotherSession = claim.Ownership with { WorkerSessionId = new RuntimeWorkerSessionId(Guid.NewGuid()) };
        exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.HeartbeatAsync(anotherSession, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, exception.Code);
    }

    [TestMethod]
    public async Task ExpiredLeaseInterruptsAttemptAndFailsRunAsWorkerLost()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(46));

        var expired = await fixture.Service.ExpireLeasesAsync(10, default);

        Assert.AreEqual(1, expired.Count);
        Assert.AreEqual(RuntimeAssignmentState.Failed, expired[0].Assignment.State);
        Assert.AreEqual(RuntimeAssignmentAttemptState.Interrupted, expired[0].Assignment.CurrentAttempt!.State);
        Assert.AreEqual("worker_lost", expired[0].Assignment.CurrentAttempt!.ErrorCode);
        var run = await fixture.Runs.GetAsync(Workspace, fixture.RunId, default);
        Assert.IsNotNull(run);
        Assert.AreEqual(RuntimeRunState.Failed, run.Value.Status.State);
        Assert.AreEqual("worker_lost", run.Value.Status.ErrorCode);
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.HeartbeatAsync(claim.Ownership, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, exception.Code);
        Assert.IsNull(await fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public async Task CancellationPersistedBeforeCompletionWinsTheTerminalRace()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        await fixture.Service.RequestCancellationAsync(Workspace, claim.Assignment.Id, default);

        var terminal = await fixture.Service.CompleteAsync(
            claim.Ownership,
            new RuntimeAssignmentTerminalCommand(RuntimeAssignmentTerminalOutcome.Succeeded, Guid.NewGuid(), "too late"),
            default);

        Assert.AreEqual(RuntimeRunState.Cancelled, terminal.RunState);
        Assert.AreEqual(RuntimeAssignmentState.Cancelled, terminal.Assignment.State);
        Assert.AreEqual(RuntimeAssignmentAttemptState.Interrupted, terminal.Assignment.CurrentAttempt!.State);
    }

    [TestMethod]
    public async Task CompletionBeforeCancellationRemainsCompletedAndTerminalReplayIsIdempotent()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        var eventId = Guid.NewGuid();
        var command = new RuntimeAssignmentTerminalCommand(RuntimeAssignmentTerminalOutcome.Succeeded, eventId, "done");

        var first = await fixture.Service.CompleteAsync(claim.Ownership, command, default);
        var replay = await fixture.Service.CompleteAsync(claim.Ownership, command, default);
        var afterCancellation = await fixture.Service.RequestCancellationAsync(Workspace, claim.Assignment.Id, default);

        Assert.AreEqual(RuntimeRunState.Succeeded, first.RunState);
        Assert.IsFalse(first.IdempotentReplay);
        Assert.IsTrue(replay.IdempotentReplay);
        Assert.AreEqual(RuntimeAssignmentState.Succeeded, afterCancellation.Value.State);
        var events = await fixture.Runs.ListEventsAsync(Workspace, fixture.RunId, 0, default);
        Assert.AreEqual(1, events.Count(value => value.EventId == eventId));
        var conflict = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.CompleteAsync(
            claim.Ownership,
            command with { EventId = Guid.NewGuid() },
            default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.TerminalConflict, conflict.Code);
    }

    [TestMethod]
    public async Task CancellationBeforeExpiryProducesCancelledRunAndInterruptedAttempt()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        await fixture.Service.RequestCancellationAsync(Workspace, claim.Assignment.Id, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(46));

        var expired = await fixture.Service.ExpireLeasesAsync(10, default);

        Assert.AreEqual(RuntimeRunState.Cancelled, expired.Single().RunState);
        Assert.AreEqual(RuntimeAssignmentAttemptState.Interrupted, expired.Single().Assignment.CurrentAttempt!.State);
        Assert.IsNull(expired.Single().Assignment.CurrentAttempt!.ErrorCode);
    }

    [TestMethod]
    public async Task AssignmentOwnershipAndLeaseSurviveStoreRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-assignment-{Guid.NewGuid():N}.db");
        try
        {
            RuntimeAssignmentId assignmentId;
            DateTimeOffset expiresAt;
            RuntimeAssignmentOwnershipProof ownership;
            await using (var first = await AssignmentFixture.CreateAsync(path))
            {
                await first.CreateAssignmentAsync();
                var claim = await first.ClaimRequiredAsync();
                assignmentId = claim.Assignment.Id;
                expiresAt = claim.Assignment.CurrentAttempt!.LeaseExpiresAt;
                ownership = claim.Ownership;
            }

            await using var second = await AssignmentFixture.CreateAsync(path);
            var stored = await second.Assignments.GetAsync(Workspace, assignmentId, default);
            Assert.IsNotNull(stored);
            Assert.AreEqual(RuntimeAssignmentState.Assigned, stored.Value.State);
            Assert.AreEqual(expiresAt, stored.Value.CurrentAttempt!.LeaseExpiresAt);
            var authorized = await second.Service.AuthorizeAsync(ownership, default);
            Assert.AreEqual(assignmentId, authorized.Assignment.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ClaimRequiresCompatibleRuntimeAndExecutionMaterialVersions()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();

        var incompatible = await fixture.Service.ClaimNextAsync(
            new RuntimeWorkerId(Guid.NewGuid()),
            new RuntimeWorkerSessionId(Guid.NewGuid()),
            "microsoft-agent-framework",
            new HashSet<string>(StringComparer.Ordinal) { "2.0" },
            new HashSet<string>(StringComparer.Ordinal) { "1.0" },
            default);

        Assert.IsNull(incompatible);
        Assert.IsNotNull(await fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    private sealed class AssignmentFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string databasePath;
        private readonly bool ownsDatabase;

        private AssignmentFixture(ServiceProvider provider, MutableTimeProvider clock, string databasePath, bool ownsDatabase)
        {
            this.provider = provider;
            this.databasePath = databasePath;
            this.ownsDatabase = ownsDatabase;
            Clock = clock;
            Runs = provider.GetRequiredService<IRuntimeRunStore>();
            Assignments = provider.GetRequiredService<IRuntimeWorkerAssignmentStore>();
            Service = provider.GetRequiredService<RuntimeWorkerAssignmentService>();
        }

        public MutableTimeProvider Clock { get; }
        public IRuntimeRunStore Runs { get; }
        public IRuntimeWorkerAssignmentStore Assignments { get; }
        public RuntimeWorkerAssignmentService Service { get; }
        public string RunId { get; } = $"run-{Guid.NewGuid():N}";
        public RuntimeAssignmentId AssignmentId { get; private set; }

        public static async Task<AssignmentFixture> CreateAsync(string? databasePath = null)
        {
            var ownsDatabase = databasePath is null;
            databasePath ??= Path.Combine(Path.GetTempPath(), $"agentstration-runtime-assignment-{Guid.NewGuid():N}.db");
            var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-03T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(new RuntimeWorkerLeaseOptions());
            services.AddSingleton<RuntimeWorkerAssignmentService>();
            services.AddSqliteRuntimeRuns($"Data Source={databasePath};Pooling=False");
            var provider = services.BuildServiceProvider();
            var fixture = new AssignmentFixture(provider, clock, databasePath, ownsDatabase);
            await fixture.Runs.InitializeAsync(default);
            await fixture.Service.InitializeAsync(default);
            return fixture;
        }

        public async Task CreateAssignmentAsync()
        {
            await Runs.CreateAsync(new RuntimeRun
            {
                WorkspaceId = Workspace,
                Scope = new RuntimeRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
                Id = RunId,
                Name = RunId,
                Properties = new RuntimeRunProperties
                {
                    Agent = new RuntimeAgentReference("agent", 1),
                    Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
                    Execution = new RuntimeExecutionOptions()
                },
                Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = Clock.GetUtcNow() }
            }, default);
            var assignment = await Service.CreateAsync(Workspace, RunId, "microsoft-agent-framework", "1.0", "1.0", default);
            AssignmentId = assignment.Value.Id;
        }

        public Task<ClaimedRuntimeWorkerAssignment?> ClaimAsync(Guid workerId, Guid sessionId) => Service.ClaimNextAsync(
            new RuntimeWorkerId(workerId),
            new RuntimeWorkerSessionId(sessionId),
            "microsoft-agent-framework",
            new HashSet<string>(StringComparer.Ordinal) { "1.0" },
            new HashSet<string>(StringComparer.Ordinal) { "1.0" },
            default);

        public async Task<ClaimedRuntimeWorkerAssignment> ClaimRequiredAsync() =>
            await ClaimAsync(Guid.NewGuid(), Guid.NewGuid()) ?? throw new AssertFailedException("Expected an assignment claim.");

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            if (ownsDatabase && File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
