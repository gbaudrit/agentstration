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
    public async Task SuspendedFlowAssignmentCanBeRequeuedWithANewAttemptAndFence()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var runId = $"flowrun-{Guid.NewGuid():N}";
        var created = await fixture.Service.CreateAsync(Workspace, Guid.NewGuid(),
            RuntimeAssignmentTargetKind.FlowRun, runId, "microsoft-agent-framework", "1.0", "1.0",
            $"material-{runId}", "sha256:flow", default);
        var first = await fixture.ClaimRequiredAsync();
        await fixture.Service.CompleteAsync(first.Ownership,
            new(RuntimeAssignmentTerminalOutcome.Succeeded, Guid.NewGuid()), default);

        var pending = await fixture.Service.RequeueAsync(Workspace, created.Value.Id, default);
        var second = await fixture.ClaimRequiredAsync();

        Assert.AreEqual(RuntimeAssignmentState.Pending, pending.Value.State);
        Assert.AreEqual(2, second.Assignment.Attempts.Count);
        Assert.AreEqual(first.Ownership.FencingGeneration + 1, second.Ownership.FencingGeneration);
        Assert.AreNotEqual(first.Ownership.AttemptId, second.Ownership.AttemptId);
        var stale = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() =>
            fixture.Service.HeartbeatAsync(first.Ownership, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, stale.Code);
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
            1,
            default);

        Assert.IsNull(incompatible);
        Assert.IsNotNull(await fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public async Task WorkerCapacityIsEnforcedByTheAtomicStoreClaim()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        await fixture.CreateAssignmentAsync($"run-{Guid.NewGuid():N}");
        var workerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var claims = await Task.WhenAll(
            fixture.ClaimAsync(workerId, sessionId, 1),
            fixture.ClaimAsync(workerId, sessionId, 1));

        Assert.AreEqual(1, claims.Count(value => value is not null));
        Assert.IsNotNull(await fixture.ClaimAsync(Guid.NewGuid(), Guid.NewGuid(), 1));
    }

    [TestMethod]
    public async Task RegisteringReplacementSessionInterruptsFormerOwnership()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var workerId = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var claim = await fixture.ClaimAsync(workerId, firstSession, 1)
            ?? throw new AssertFailedException("Expected an assignment claim.");

        var interrupted = await fixture.Service.InterruptSupersededSessionsAsync(
            new RuntimeWorkerId(workerId), new RuntimeWorkerSessionId(Guid.NewGuid()), default);

        Assert.HasCount(1, interrupted);
        Assert.AreEqual(RuntimeAssignmentState.Failed, interrupted[0].Assignment.State);
        Assert.AreEqual(RuntimeAssignmentAttemptState.Interrupted, interrupted[0].Assignment.CurrentAttempt!.State);
        Assert.AreEqual("worker_lost", interrupted[0].Assignment.CurrentAttempt!.ErrorCode);
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() =>
            fixture.Service.HeartbeatAsync(claim.Ownership, default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.NotOwned, exception.Code);
    }

    [TestMethod]
    public async Task ReplacementSessionWakesAndRejectsFormerSessionLongPoll()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var workerId = new RuntimeWorkerId(Guid.NewGuid());
        var firstSession = new RuntimeWorkerSessionId(Guid.NewGuid());
        var replacementSession = new RuntimeWorkerSessionId(Guid.NewGuid());
        RuntimeWorkerCapabilityRegistration[] capabilities =
        [
            new("microsoft-agent-framework", "1.0",
                new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "test-maf")
        ];
        await fixture.Dispatch.RegisterAsync(workerId, firstSession, "test-worker", 1, capabilities, default);
        var waiting = fixture.Dispatch.ClaimAsync(workerId, firstSession, 1, 5, default);
        await Task.Delay(25);

        await fixture.Dispatch.RegisterAsync(workerId, replacementSession, "test-worker", 1, capabilities, default);

        var exception = await Assert.ThrowsExactlyAsync<RuntimeWorkerDispatchException>(() => waiting);
        Assert.AreEqual("worker_session_superseded", exception.Code);
    }

    [TestMethod]
    public async Task LongPollRechecksDurableWorkWhenAvailabilitySignalArrives()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var workerId = new RuntimeWorkerId(Guid.NewGuid());
        var sessionId = new RuntimeWorkerSessionId(Guid.NewGuid());
        await fixture.Dispatch.RegisterAsync(workerId, sessionId, "test-worker", 1,
        [
            new RuntimeWorkerCapabilityRegistration(
                "microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "test-maf")
        ], default);
        var waiting = fixture.Dispatch.ClaimAsync(workerId, sessionId, 1, 5, default);

        await Task.Delay(100);
        await fixture.CreateAssignmentAsync();
        var claimed = await waiting;

        Assert.IsNotNull(claimed);
        Assert.AreEqual(fixture.RunId, claimed.Assignment.TargetRunId);
    }

    [TestMethod]
    public async Task CompatibleWorkersWithDifferentImplementationsServeTheSameRuntimeFamily()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        await fixture.CreateAssignmentAsync($"run-{Guid.NewGuid():N}");
        var firstWorker = new RuntimeWorkerId(Guid.NewGuid());
        var firstSession = new RuntimeWorkerSessionId(Guid.NewGuid());
        var secondWorker = new RuntimeWorkerId(Guid.NewGuid());
        var secondSession = new RuntimeWorkerSessionId(Guid.NewGuid());
        await fixture.Dispatch.RegisterAsync(firstWorker, firstSession, "worker-1.0", 1,
        [
            new RuntimeWorkerCapabilityRegistration(
                "microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "maf-1.0")
        ], default);
        await fixture.Dispatch.RegisterAsync(secondWorker, secondSession, "worker-2.0", 1,
        [
            new RuntimeWorkerCapabilityRegistration(
                "microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "maf-2.0")
        ], default);

        var claims = await Task.WhenAll(
            fixture.Dispatch.ClaimAsync(firstWorker, firstSession, 1, 0, default),
            fixture.Dispatch.ClaimAsync(secondWorker, secondSession, 1, 0, default));

        Assert.IsTrue(claims.All(value => value is not null));
        Assert.AreEqual(2, claims.Select(value => value!.Assignment.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task IdleClaimPollingRefreshesObservableWorkerPresence()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var workerId = new RuntimeWorkerId(Guid.NewGuid());
        var sessionId = new RuntimeWorkerSessionId(Guid.NewGuid());
        var registered = await fixture.Dispatch.RegisterAsync(workerId, sessionId, "worker-1.0", 2,
        [
            new RuntimeWorkerCapabilityRegistration(
                "microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "maf-1.0")
        ], default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));

        Assert.IsNull(await fixture.Dispatch.ClaimAsync(workerId, sessionId, 0, 0, default));

        var observed = fixture.Dispatch.ListRegistrations().Single();
        Assert.AreEqual(registered.RegisteredAt, observed.RegisteredAt);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), observed.LastSeenAt);
    }

    [TestMethod]
    public async Task AssignmentQueryFiltersByWorkspaceWorkerAndState()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var workerId = Guid.NewGuid();
        var claim = await fixture.ClaimAsync(workerId, Guid.NewGuid())
            ?? throw new AssertFailedException("Expected an assignment claim.");

        var visible = await fixture.Assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            WorkspaceId = Workspace,
            WorkerId = new RuntimeWorkerId(workerId),
            States = new HashSet<RuntimeAssignmentState> { RuntimeAssignmentState.Assigned }
        }, default);
        var anotherWorkspace = await fixture.Assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            WorkspaceId = new WorkspaceId(Guid.NewGuid())
        }, default);

        Assert.HasCount(1, visible);
        Assert.AreEqual(claim.Assignment.Id, visible[0].Value.Id);
        Assert.IsEmpty(anotherWorkspace);
    }

    [TestMethod]
    public async Task AssignmentQueryRetainsCompletedHistoryForTheWorker()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var workerId = Guid.NewGuid();
        var claim = await fixture.ClaimAsync(workerId, Guid.NewGuid())
            ?? throw new AssertFailedException("Expected an assignment claim.");
        await fixture.Service.CompleteAsync(claim.Ownership,
            new RuntimeAssignmentTerminalCommand(RuntimeAssignmentTerminalOutcome.Succeeded, Guid.NewGuid(), "done"), default);

        var history = await fixture.Assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            WorkerId = new RuntimeWorkerId(workerId)
        }, default);

        Assert.HasCount(1, history);
        Assert.AreEqual(RuntimeAssignmentState.Succeeded, history[0].Value.State);
        Assert.AreEqual(workerId, history[0].Value.Attempts.Single().WorkerId.Value);
    }

    [TestMethod]
    public async Task AvailabilitySignalClosesLostWakeWindowAndCoalescesDuplicatePulses()
    {
        var signal = new RuntimeAssignmentAvailabilitySignal();
        var beforePulses = signal.Capture();

        signal.Pulse();
        signal.Pulse();
        await signal.WaitForChangeAsync(beforePulses, TimeSpan.FromSeconds(1), default);

        var afterPulses = signal.Capture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var waiting = signal.WaitForChangeAsync(afterPulses, TimeSpan.FromSeconds(1), cancellation.Token);
        await Task.Delay(25, cancellation.Token);
        Assert.IsFalse(waiting.IsCompleted, "Previously coalesced wake-ups must not leak into a new wait.");
        signal.Pulse();
        await waiting;
    }

    [TestMethod]
    public async Task RestartedDispatcherRequiresRegistrationThenFindsPreexistingDurableWork()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var workerId = new RuntimeWorkerId(Guid.NewGuid());
        var sessionId = new RuntimeWorkerSessionId(Guid.NewGuid());
        var restarted = new RuntimeWorkerDispatchService(
            fixture.Service, fixture.Availability, fixture.Clock, new RuntimeWorkerDispatchOptions());

        var exception = await Assert.ThrowsExactlyAsync<RuntimeWorkerDispatchException>(() =>
            restarted.ClaimAsync(workerId, sessionId, 1, 0, default));
        Assert.AreEqual("worker_not_registered", exception.Code);
        await restarted.RegisterAsync(workerId, sessionId, "restarted-worker", 1,
        [
            new RuntimeWorkerCapabilityRegistration(
                "microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, null)
        ], default);

        var claimed = await restarted.ClaimAsync(workerId, sessionId, 1, 0, default);

        Assert.IsNotNull(claimed);
        Assert.AreEqual(fixture.RunId, claimed.Assignment.TargetRunId);
    }

    [TestMethod]
    public async Task AssignmentAuthorizesMultipleTurnsWithOneInitialAttemptEach()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();

        var first = await fixture.Service.OpenTurnAsync(claim.Ownership, fixture.RunId, null, "agent", default);
        var second = await fixture.Service.OpenTurnAsync(claim.Ownership, fixture.RunId, null, "agent", default);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreNotEqual(first.AttemptId, second.AttemptId);
        Assert.AreEqual(1, first.AttemptNumber);
        Assert.AreEqual(1, second.AttemptNumber);
        var stored = await fixture.Assignments.GetAsync(Workspace, claim.Assignment.Id, default);
        Assert.IsNotNull(stored);
        Assert.HasCount(2, stored.Value.Turns);
    }

    [TestMethod]
    public async Task EventReplayIsIdempotentAndRequiresAttemptLocalContiguousOrdering()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        var turn = await fixture.Service.OpenTurnAsync(claim.Ownership, fixture.RunId, null, "agent", default);
        var executionEvent = new RuntimeAssignmentExecutionEvent
        {
            EventId = Guid.NewGuid(),
            AttemptEventSequence = 1,
            OccurredAt = fixture.Clock.GetUtcNow(),
            Kind = "TurnStarted",
            RunId = fixture.RunId,
            TurnId = turn.Id,
            TurnAttemptId = turn.AttemptId,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { value = "stable" })
        };

        var accepted = await fixture.Service.AppendEventsAsync(claim.Ownership, [executionEvent], default);
        var replay = await fixture.Service.AppendEventsAsync(claim.Ownership, [executionEvent], default);

        Assert.AreEqual(1, accepted.AcceptedThroughSequence);
        Assert.AreEqual(executionEvent.EventId, replay.DuplicateEventIds.Single());
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() => fixture.Service.AppendEventsAsync(
            claim.Ownership,
            [executionEvent with { EventId = Guid.NewGuid(), AttemptEventSequence = 3 }],
            default));
        Assert.AreEqual(RuntimeAssignmentErrorCodes.InvalidEventSequence, exception.Code);
    }

    [TestMethod]
    public async Task CheckpointRequiresCurrentOwnershipAndSurvivesStoreRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-runtime-checkpoint-{Guid.NewGuid():N}.db");
        try
        {
            RuntimeAssignmentOwnershipProof ownership;
            await using (var first = await AssignmentFixture.CreateAsync(path))
            {
                await first.CreateAssignmentAsync();
                var claim = await first.ClaimRequiredAsync();
                ownership = claim.Ownership;
                await first.Service.StoreCheckpointAsync(ownership, "checkpoint-1", "maf-json-v1", "sha256:test",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { state = 1 }), default);
            }

            await using var second = await AssignmentFixture.CreateAsync(path);
            var checkpoint = await second.Service.GetCheckpointAsync(ownership, "checkpoint-1", default);
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(1, checkpoint.Payload.GetProperty("state").GetInt32());
            second.Clock.Advance(TimeSpan.FromSeconds(46));
            var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() =>
                second.Service.GetCheckpointAsync(ownership, "checkpoint-1", default));
            Assert.AreEqual(RuntimeAssignmentErrorCodes.LeaseExpired, exception.Code);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task GovernedSideEffectIsRejectedInsideLeaseSafetyMargin()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(40));

        var exception = await Assert.ThrowsExactlyAsync<RuntimeAssignmentException>(() =>
            fixture.Execution.StoreArtifactAsync(claim.Ownership, "result.txt", "text/plain", "value"u8.ToArray(), default));

        Assert.AreEqual(RuntimeAssignmentErrorCodes.LeaseTooShort, exception.Code);
    }

    [TestMethod]
    public async Task PersistedHeartbeatExtendsAnInFlightGovernedOperation()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        var operation = fixture.Execution.StoreArtifactAsync(
            claim.Ownership, "result.txt", "text/plain", "value"u8.ToArray(), default);
        await fixture.Operations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        _ = await fixture.Service.HeartbeatAsync(claim.Ownership, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));

        Assert.IsFalse(operation.IsCompleted,
            "The persisted heartbeat must keep the governed operation alive beyond its original deadline.");
        fixture.Operations.Release.TrySetResult();
        var artifact = await operation;
        Assert.AreEqual("result.txt", artifact.Name);
    }

    [TestMethod]
    public async Task CancellationRequestRevokesAnInFlightLeaseGuard()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.CreateAssignmentAsync();
        var claim = await fixture.ClaimRequiredAsync();
        using var guard = fixture.LeaseGuards.Register(claim.Ownership,
            claim.Assignment.CurrentAttempt!.LeaseExpiresAt, default);

        _ = await fixture.Service.RequestCancellationAsync(Workspace, claim.Assignment.Id, default);

        Assert.IsTrue(guard.Token.IsCancellationRequested);
    }

    [TestMethod]
    public async Task RootFlowAssignmentCanBeClaimedWithoutARuntimeRunMirror()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var flowRunId = $"flowrun-{Guid.NewGuid():N}";
        var tenantId = Guid.NewGuid();
        _ = await fixture.Service.CreateAsync(Workspace, tenantId, RuntimeAssignmentTargetKind.FlowRun,
            flowRunId, "microsoft-agent-framework", "1.0", "1.0", $"material-{flowRunId}",
            "sha256:flow", default);

        var claim = await fixture.ClaimRequiredAsync();

        Assert.AreEqual(RuntimeAssignmentTargetKind.FlowRun, claim.Assignment.TargetKind);
        Assert.AreEqual(flowRunId, claim.Assignment.TargetRunId);
        Assert.AreEqual(tenantId, claim.Assignment.TenantId);
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
            Dispatch = provider.GetRequiredService<RuntimeWorkerDispatchService>();
            Availability = provider.GetRequiredService<RuntimeAssignmentAvailabilitySignal>();
            LeaseGuards = provider.GetRequiredService<RuntimeAssignmentLeaseGuardRegistry>();
            Operations = provider.GetRequiredService<ControlledOperationGateway>();
            Execution = provider.GetRequiredService<RuntimeWorkerExecutionService>();
        }

        public MutableTimeProvider Clock { get; }
        public IRuntimeRunStore Runs { get; }
        public IRuntimeWorkerAssignmentStore Assignments { get; }
        public RuntimeWorkerAssignmentService Service { get; }
        public RuntimeWorkerDispatchService Dispatch { get; }
        public RuntimeAssignmentAvailabilitySignal Availability { get; }
        public RuntimeAssignmentLeaseGuardRegistry LeaseGuards { get; }
        public ControlledOperationGateway Operations { get; }
        public RuntimeWorkerExecutionService Execution { get; }
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
            services.AddSingleton(new RuntimeWorkerDispatchOptions());
            services.AddSingleton<RuntimeAssignmentAvailabilitySignal>();
            services.AddSingleton<RuntimeAssignmentLeaseGuardRegistry>();
            services.AddSingleton<RuntimeWorkerAssignmentService>();
            services.AddSingleton<RuntimeWorkerDispatchService>();
            services.AddSingleton<IRuntimeExecutionMaterialResolver, UnusedMaterialResolver>();
            services.AddSingleton<ControlledOperationGateway>();
            services.AddSingleton<IRuntimeWorkerOperationGateway>(provider =>
                provider.GetRequiredService<ControlledOperationGateway>());
            services.AddSingleton<RuntimeWorkerExecutionService>();
            services.AddSqliteRuntimeRuns($"Data Source={databasePath};Pooling=False");
            var provider = services.BuildServiceProvider();
            var fixture = new AssignmentFixture(provider, clock, databasePath, ownsDatabase);
            await fixture.Runs.InitializeAsync(default);
            await fixture.Service.InitializeAsync(default);
            return fixture;
        }

        public async Task CreateAssignmentAsync(string? runId = null)
        {
            runId ??= RunId;
            await Runs.CreateAsync(new RuntimeRun
            {
                WorkspaceId = Workspace,
                Scope = new RuntimeRunScope(Guid.NewGuid(), Workspace, Guid.NewGuid()),
                Id = runId,
                Name = runId,
                Properties = new RuntimeRunProperties
                {
                    Agent = new RuntimeAgentReference("agent", 1),
                    Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] },
                    Execution = new RuntimeExecutionOptions()
                },
                Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = Clock.GetUtcNow() }
            }, default);
            var assignment = await Service.CreateAsync(Workspace, runId, "microsoft-agent-framework", "1.0", "1.0",
                $"material-{runId}", "sha256:test", default);
            AssignmentId = assignment.Value.Id;
        }

        public Task<ClaimedRuntimeWorkerAssignment?> ClaimAsync(Guid workerId, Guid sessionId, int maximumConcurrentAssignments = 1) => Service.ClaimNextAsync(
            new RuntimeWorkerId(workerId),
            new RuntimeWorkerSessionId(sessionId),
            "microsoft-agent-framework",
            new HashSet<string>(StringComparer.Ordinal) { "1.0" },
            new HashSet<string>(StringComparer.Ordinal) { "1.0" },
            maximumConcurrentAssignments,
            default);

        public async Task<ClaimedRuntimeWorkerAssignment> ClaimRequiredAsync() =>
            await ClaimAsync(Guid.NewGuid(), Guid.NewGuid()) ?? throw new AssertFailedException("Expected an assignment claim.");

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            if (ownsDatabase && File.Exists(databasePath)) File.Delete(databasePath);
        }

        private sealed class UnusedMaterialResolver : IRuntimeExecutionMaterialResolver
        {
            public Task<RuntimeExecutionMaterial> ResolveAsync(RuntimeWorkerAssignment assignment, CancellationToken cancellationToken) =>
                Task.FromException<RuntimeExecutionMaterial>(new AssertFailedException("Material resolution was not expected."));

            public Task<RuntimeFlowStepMaterial> ResolveStepAsync(RuntimeWorkerAssignment assignment, string flowRunId,
                string flowVersion, string flowDefinitionHash, string stepDefinitionId, CancellationToken cancellationToken) =>
                Task.FromException<RuntimeFlowStepMaterial>(new AssertFailedException("Step resolution was not expected."));
        }

        public sealed class ControlledOperationGateway : IRuntimeWorkerOperationGateway
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<RuntimeGovernedModelResponse> InvokeModelAsync(RuntimeGovernedModelRequest request, CancellationToken cancellationToken) => Unexpected<RuntimeGovernedModelResponse>();
            public Task<System.Text.Json.JsonElement?> InvokeToolAsync(RuntimeGovernedToolRequest request, CancellationToken cancellationToken) => Unexpected<System.Text.Json.JsonElement?>();
            public async Task<RuntimeGovernedArtifact> StoreArtifactAsync(WorkspaceId workspaceId,
                RuntimeAssignmentId assignmentId, string name, string contentType, byte[] content,
                CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return new(Guid.NewGuid(), name, contentType, content.LongLength, content);
            }
            public Task<RuntimeGovernedArtifact?> GetArtifactAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId, Guid artifactId, CancellationToken cancellationToken) => Unexpected<RuntimeGovernedArtifact?>();
            public Task<RuntimeGovernedChildFlow> CreateOrGetChildFlowAsync(WorkspaceId workspaceId, string parentRunId, string stepDefinitionId, System.Text.Json.JsonElement input, CancellationToken cancellationToken) => Unexpected<RuntimeGovernedChildFlow>();

            private static Task<T> Unexpected<T>() => Task.FromException<T>(new AssertFailedException("A governed operation was not expected."));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];

        public override DateTimeOffset GetUtcNow() => current;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (gate) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            current = current.Add(duration);
            while (true)
            {
                ManualTimer[] due;
                lock (gate) due = timers.Where(value => value.IsDue(current)).ToArray();
                if (due.Length == 0) return;
                foreach (var timer in due) timer.Fire(current);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (gate) timers.Remove(timer);
        }

        private sealed class ManualTimer(
            MutableTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? dueAt;
            private TimeSpan period = Timeout.InfiniteTimeSpan;
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow().Add(dueTime);
                period = newPeriod;
                return true;
            }

            public bool IsDue(DateTimeOffset now) => !disposed && dueAt is { } due && due <= now;

            public void Fire(DateTimeOffset now)
            {
                if (!IsDue(now)) return;
                dueAt = period == Timeout.InfiniteTimeSpan ? null : now.Add(period);
                callback(state);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
