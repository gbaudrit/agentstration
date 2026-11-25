using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class CodingUsageAccountingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void NativeAccountingDeduplicatesStreamingAndFinalReceiptsButCountsDistinctTurnsWithinOneInvocation()
    {
        var fixture = Create(); var one = Observation(fixture, "turn-1", 13, 7, 23); var two = Observation(fixture, "turn-2", 3, 1, 5);
        var final = new CodingNativeResult(fixture.Coordinates, "base", null, null, "", 0, [], [one, two]);
        var assignment = fixture.Assignment with { ExecutionEvents = [Event(new { codingConsumption = one }), Event(new { codingConsumption = two }), Event(new { codingResult = final })] };
        var result = CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt, assignment);
        Assert.IsTrue(result.IsSuccess, result.ErrorCode); Assert.HasCount(1, result.Value!);
        var usage = result.Value!.Single(); Assert.AreEqual(16L, usage.Totals.Direct.InputTokens); Assert.AreEqual(8L, usage.Totals.Direct.OutputTokens);
        Assert.AreEqual(28L, usage.Totals.Direct.TotalTokens); Assert.AreEqual(1, usage.Totals.Direct.InvocationCount);
        Assert.HasCount(2, usage.Observations); Assert.HasCount(3, usage.Source.EventIds);
    }
    [TestMethod]
    public void NativeAccountingRejectsConflictingObservationsForeignCoordinatesAndUnboundedCounters()
    {
        var fixture = Create(); var observation = Observation(fixture, "turn-1", 13, 7, null);
        foreach (var invalid in new[] { observation with { InputTokens = 14 }, observation with { Coordinates = fixture.Coordinates with { PlanId = WorkPlanId.New() } }, observation with { InputTokens = long.MaxValue } })
        {
            var result = CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt,
                fixture.Assignment with { ExecutionEvents = [Event(new { codingConsumption = observation }), Event(new { codingConsumption = invalid })] });
            Assert.IsFalse(result.IsSuccess);
        }
    }
    [TestMethod]
    public void NativeAccountingKeepsUnknownCardinalitySeparateFromNeverStartedAndMissingProviderTotals()
    {
        var fixture = Create();
        var missing = CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt, fixture.Assignment).Value!.Single();
        Assert.IsFalse(missing.Source.InvocationCountKnown); Assert.IsNull(missing.Totals.Direct.TotalTokens); Assert.AreEqual(0, missing.Totals.Direct.InvocationCount);
        var notStarted = CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt, fixture.Assignment with { Attempts = [] }).Value!.Single();
        Assert.IsTrue(notStarted.Source.InvocationCountKnown); Assert.AreEqual(WorkUsageSourceKind.NotStarted, notStarted.Source.Kind);
        var observation = Observation(fixture, "turn-1", 13, 7, null);
        var partial = CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt,
            fixture.Assignment with { ExecutionEvents = [Event(new { codingConsumption = observation })] }).Value!.Single();
        Assert.IsNull(partial.Totals.Direct.TotalTokens); Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, partial.Totals.Direct.Coverage);
        Assert.AreEqual(1, partial.Totals.Direct.InvocationCount);
        Assert.AreEqual("usage_source_expired", CodingUsageAccounting.Read(fixture.Scope, fixture.Coordinates.ProjectId, fixture.Coordinates.PlanId, fixture.Attempt,
            fixture.Assignment with { TransientPayloadsAvailable = false }).ErrorCode);
    }
    private sealed record Fixture(FlowRunScope Scope, WorkPlanAttempt Attempt, CodingInvocationCoordinates Coordinates, RuntimeWorkerAssignment Assignment);
    private static Fixture Create()
    {
        var workspace = new WorkspaceId(Guid.NewGuid()); var principal = Guid.NewGuid(); var scope = new FlowRunScope(Guid.NewGuid(), workspace, principal);
        var attempt = new WorkPlanAttempt(Guid.NewGuid(), new(Guid.NewGuid()), 1, new string('a', 64), 1, new string('b', 64), new(Guid.NewGuid()), "fixture-run",
            new(new FlowId("fixture")), JsonSerializer.SerializeToElement(new { }), [], WorkPlanAttemptStatus.Failed, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var assignmentId = Guid.NewGuid(); var nativeAttempt = Guid.NewGuid();
        var coordinates = new CodingInvocationCoordinates(workspace, principal, WorkProjectId.New(), WorkPlanId.New(), 1, attempt.PlanDigest, attempt.TaskId, 1,
            attempt.TaskDigest, attempt.Id, attempt.WorkItemId, attempt.FlowRunId, assignmentId, nativeAttempt, nativeAttempt);
        var assignment = new RuntimeWorkerAssignment
        {
            WorkspaceId = workspace,
            Id = new(assignmentId),
            TargetKind = RuntimeAssignmentTargetKind.FlowRun,
            TargetRunId = attempt.FlowRunId,
            RuntimeCapability = "coding-cli",
            RuntimeCapabilityVersion = "1.0",
            ExecutionMaterialVersion = "1.0",
            ExecutionMaterialId = "fixture",
            ExecutionMaterialDigest = new string('c', 64),
            State = RuntimeAssignmentState.Failed,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Attempts = [new() { Id = new(nativeAttempt), WorkerId = new(Guid.NewGuid()), WorkerSessionId = new(Guid.NewGuid()), FencingGeneration = 1,
                AcquiredAt = DateTimeOffset.UnixEpoch, LeaseExpiresAt = DateTimeOffset.UnixEpoch, LastHeartbeatAt = DateTimeOffset.UnixEpoch, State = RuntimeAssignmentAttemptState.Failed }]
        };
        return new(scope, attempt, coordinates, assignment);
    }
    private static CodingConsumptionObservation Observation(Fixture fixture, string id, long? input, long? output, long? total) =>
        new(id, fixture.Coordinates, CodingConsumptionAvailability.Partial, input, null, output, null, DateTimeOffset.UnixEpoch) { ProviderTotalTokens = total };
    private static RuntimeAssignmentExecutionEvent Event<T>(T payload) => new()
    {
        EventId = Guid.NewGuid(),
        AttemptEventSequence = 1,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Kind = "Diagnostic",
        RunId = "fixture-run",
        Payload = JsonSerializer.SerializeToElement(payload, Json)
    };
}
