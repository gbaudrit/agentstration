using Agentstration.Flows;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class WorkUsageFormatTests
{
    [TestMethod]
    public void AggregateFieldsDoNotTurnPartialInvocationCoverageIntoCompleteCoverage()
    {
        var total = FlowRunTokenUsageAggregation.Combine([
            FlowRunTokenUsageAggregation.Invocation(10, null, null),
            FlowRunTokenUsageAggregation.Invocation(null, 5, 15)]);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, total.Coverage);
        var record = Record(total);
        Assert.IsTrue(WorkUsageFormat.Valid(record));
        Assert.IsFalse(WorkUsageFormat.Valid(record with { Measurement = WorkUsageMeasurement.Unknown }), "Known reported counters cannot be relabeled as unknown zero.");
    }

    [TestMethod]
    public void PortableAttributionAndCorrectionLineageAreExplicitAndCannotBeHalfSpecified()
    {
        var original = Record(FlowRunTokenUsageAggregation.Invocation(20, 5, null));
        Assert.IsTrue(WorkUsageFormat.Valid(original));
        var attributed = original with { ProjectId = new WorkSharingProjectId(Guid.NewGuid()) };
        Assert.IsFalse(WorkUsageFormat.Valid(attributed));
        attributed = attributed with { ParticipantId = new WorkParticipantId(Guid.NewGuid()) };
        Assert.IsTrue(WorkUsageFormat.Valid(attributed));
        var correction = attributed with
        {
            Id = new(Guid.NewGuid()),
            Revision = 2,
            PreviousId = attributed.Id,
            PreviousDigest = WorkUsageFormat.Digest(attributed),
            Correction = new(Guid.NewGuid(), new string('b', 64), "provider_correction", "receipt:fixture-1")
        };
        Assert.IsTrue(WorkUsageFormat.Valid(correction));
        Assert.IsFalse(WorkUsageFormat.Valid(correction with { PreviousId = correction.Id }));
        Assert.IsFalse(WorkUsageFormat.Valid(correction with { PreviousDigest = null }));
        Assert.IsFalse(WorkUsageFormat.Valid(correction with { Correction = correction.Correction with { EvidenceReference = "https://user:credential@example.test/receipt" } }));
    }

    private static WorkUsageRecord Record(FlowRunTokenUsageTotal total)
    {
        var id = new WorkUsageId(Guid.NewGuid());
        return new(id, id, 1, WorkPlanId.New(), 1, new string('a', 64), new(Guid.NewGuid()), 1, new string('b', 64),
            new(WorkUsageSourceKind.FlowRun, "flow-run/1.0", Guid.NewGuid(), Guid.NewGuid(), "run-fixture", WorkPlanAttemptStatus.Completed),
            WorkUsageMeasurement.Measured, new(total, total), DateTimeOffset.UnixEpoch);
    }
}
