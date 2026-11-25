using Agentstration.Flows;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class WorkUsageAccountingTests
{
    [TestMethod]
    public void UsageRollupsChooseOneFlowBoundaryAndExplicitDistinctPlansWithoutFollowingDependencies()
    {
        var parent = new WorkTaskId(Guid.NewGuid()); var first = Record(); var second = Record();
        first = first with
        {
            AncestorTaskIds = [parent],
            Totals = new(FlowRunTokenUsageAggregation.Invocation(10, 2, 15),
            FlowRunTokenUsageAggregation.Combine([FlowRunTokenUsageAggregation.Invocation(10, 2, 15), FlowRunTokenUsageAggregation.Invocation(3, 1, 6)]))
        };
        var all = WorkUsageAccounting.Summarize([first, second], new([first.PlanId, first.PlanId])).Value!;
        Assert.AreEqual(1, all.OriginalRecords); Assert.AreEqual(21L, all.Measured.Inclusive.TotalTokens); Assert.AreEqual(2, all.Measured.Inclusive.InvocationCount);
        var direct = WorkUsageAccounting.Summarize([first, second], new([], parent, true, WorkUsageFlowBoundary.Direct)).Value!;
        Assert.AreEqual(15L, direct.Measured.Direct.TotalTokens); Assert.AreEqual(1, direct.Measured.Direct.InvocationCount);
        var self = WorkUsageAccounting.Summarize([first, second], new([], parent)).Value!;
        Assert.AreEqual(0, self.OriginalRecords); Assert.IsNull(self.Measured.Direct.TotalTokens);
        var project = WorkUsageAccounting.Summarize([first, second], new([])).Value!;
        Assert.AreEqual(2, project.OriginalRecords); Assert.AreEqual(2, project.PlanIds.Count);
    }
    [TestMethod]
    public void UsageCorrectionsReplaceOnlyTheRollupAndPreserveMeasuredEstimatedUnknownSeparation()
    {
        var original = Record();
        var corrected = original with
        {
            Id = new(Guid.NewGuid()),
            Revision = 2,
            PreviousId = original.Id,
            PreviousDigest = WorkUsageFormat.Digest(original),
            Measurement = WorkUsageMeasurement.Estimated,
            Correction = new(Guid.NewGuid(), new string('e', 64), "provider_adjustment", "receipt:fixture")
        };
        var unknown = Record() with { Measurement = WorkUsageMeasurement.Unknown, Totals = new(FlowRunTokenUsageAggregation.Invocation(null, null, null), FlowRunTokenUsageAggregation.Invocation(null, null, null)) };
        var result = WorkUsageAccounting.Summarize([corrected, unknown, original], new([])).Value!;
        Assert.AreEqual(2, result.OriginalRecords); Assert.AreEqual(1, result.Corrections);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Unavailable, result.Canonical.Direct.Coverage); Assert.AreEqual(2, result.Canonical.Direct.InvocationCount);
        Assert.IsNull(result.Measured.Direct.TotalTokens); Assert.AreEqual(30L, result.Estimated.Direct.TotalTokens);
        Assert.IsNull(result.Unknown.Direct.TotalTokens); Assert.AreEqual(1, result.Unknown.Direct.InvocationCount);
        Assert.AreEqual("usage_lineage_incomplete", WorkUsageAccounting.Summarize([corrected], new([])).ErrorCode);
        Assert.AreEqual("usage_lineage_conflict", WorkUsageAccounting.Summarize([original, corrected, corrected with { Id = new(Guid.NewGuid()) }], new([])).ErrorCode);
    }
    [TestMethod]
    public void UsageRejectsDuplicateExecutionRootsAndCorrectedIdentityChanges()
    {
        var record = Record(); var duplicate = record with { Id = new(Guid.NewGuid()), RootId = new(Guid.NewGuid()) }; duplicate = duplicate with { RootId = duplicate.Id };
        Assert.AreEqual("usage_execution_duplicate", WorkUsageAccounting.Summarize([record, duplicate], new([])).ErrorCode);
        var correction = record with
        {
            Id = new(Guid.NewGuid()),
            Revision = 2,
            PreviousId = record.Id,
            PreviousDigest = WorkUsageFormat.Digest(record),
            Source = record.Source with { AttemptId = Guid.NewGuid() },
            Correction = new(Guid.NewGuid(), new string('f', 64), "adjustment", "receipt:fixture")
        };
        Assert.AreEqual("usage_lineage_conflict", WorkUsageAccounting.Summarize([record, correction], new([])).ErrorCode);
    }
    private static WorkUsageRecord Record()
    {
        var id = new WorkUsageId(Guid.NewGuid()); var total = FlowRunTokenUsageAggregation.Invocation(20, 5, 30);
        return new(id, id, 1, WorkPlanId.New(), 1, new string('a', 64), new(Guid.NewGuid()), 1, new string('b', 64),
            new(WorkUsageSourceKind.FlowRun, "flow-run/1.0", Guid.NewGuid(), Guid.NewGuid(), "fixture-run", WorkPlanAttemptStatus.Completed),
            WorkUsageMeasurement.Measured, new(total, total), DateTimeOffset.UnixEpoch);
    }
}
