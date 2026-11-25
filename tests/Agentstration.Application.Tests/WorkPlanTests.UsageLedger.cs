using Agentstration.Flows;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task UsageLedgerCapturesAtomicallyReplaysAcrossRestartAndKeepsPrivateProjectBoundaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = await UsageProjectAsync(fixture);
        // Exercise the additive initializer against an existing pre-ledger database.
        await fixture.SqlAsync("DROP TABLE WorkUsageRecords;");
        await fixture.Usage.InitializeAsync(default);
        var first = UsageEntry(fixture, project); var second = UsageEntry(fixture, project);
        Assert.AreEqual(WorkUsageSaveStatus.Saved, await fixture.Usage.CaptureAsync([first, second], default));
        Assert.AreEqual(WorkUsageSaveStatus.Replayed, await fixture.Usage.CaptureAsync([first, second], default));
        var third = UsageEntry(fixture, project);
        var changed = first with { Record = first.Record with { Measurement = WorkUsageMeasurement.Estimated } };
        changed = changed with { Digest = WorkUsageFormat.Digest(changed.Record) };
        Assert.AreEqual(WorkUsageSaveStatus.Conflict, await fixture.Usage.CaptureAsync([third, changed], default));
        Assert.IsNull(await fixture.Usage.GetAsync(fixture.Workspace, fixture.Principal, project, third.Record.Id, default));
        await fixture.ReopenAsync();
        var retained = await fixture.Usage.GetAsync(fixture.Workspace, fixture.Principal, project, first.Record.Id, default);
        Assert.IsNotNull(retained); Assert.AreEqual(first.Digest, retained.Digest);
        Assert.IsNull(await fixture.Usage.GetAsync(new(Guid.NewGuid()), fixture.Principal, project, first.Record.Id, default));
        Assert.IsNull(await fixture.Usage.GetAsync(fixture.Workspace, Guid.NewGuid(), project, first.Record.Id, default));
        Assert.IsNull(await fixture.Usage.GetAsync(fixture.Workspace, fixture.Principal, WorkProjectId.New(), first.Record.Id, default));
        var page = await fixture.Usage.ListAsync(fixture.Workspace, fixture.Principal, project, 0, 1, default);
        Assert.HasCount(1, page.Items); Assert.IsTrue(page.HasMore);
        Assert.HasCount(2, await fixture.Usage.ReadProjectAsync(fixture.Workspace, fixture.Principal, project, [], 3, default));
    }

    [TestMethod]
    public async Task UsageCorrectionCompareAndSwapKeepsOneWinningRevisionAndTheOriginalEvidence()
    {
        await using var fixture = await Fixture.CreateAsync(); var project = await UsageProjectAsync(fixture);
        await fixture.Usage.InitializeAsync(default);
        var original = UsageEntry(fixture, project);
        Assert.AreEqual(WorkUsageSaveStatus.Saved, await fixture.Usage.CaptureAsync([original], default));
        var left = UsageCorrection(original); var right = UsageCorrection(original);
        var results = await Task.WhenAll(fixture.Usage.AppendCorrectionAsync(left, default), fixture.Usage.AppendCorrectionAsync(right, default));
        Assert.AreEqual(1, results.Count(value => value == WorkUsageSaveStatus.Saved));
        Assert.AreEqual(1, results.Count(value => value == WorkUsageSaveStatus.Conflict));
        var winner = results[0] == WorkUsageSaveStatus.Saved ? left : right;
        Assert.AreEqual(WorkUsageSaveStatus.Replayed, await fixture.Usage.AppendCorrectionAsync(winner, default));
        var rows = await fixture.Usage.ReadProjectAsync(fixture.Workspace, fixture.Principal, project, [], 3, default);
        Assert.HasCount(2, rows); Assert.AreEqual(original.Digest, rows.Single(value => value.Record.Revision == 1).Digest);
        Assert.AreEqual(original.Record.Id, rows.Single(value => value.Record.Revision == 2).Record.PreviousId);
    }

    private static async Task<WorkProjectId> UsageProjectAsync(Fixture fixture)
    {
        await fixture.Projects.InitializeAsync(default);
        var id = WorkProjectId.New(); var definition = new WorkProjectDefinition("Usage fixture", null, [], []);
        var digest = WorkPlanDefinition.Digest(definition);
        var snapshot = new WorkProjectSnapshot(id, fixture.Workspace, fixture.Principal, "usage", default, definition, 1, digest, Now, Now, []);
        Assert.AreEqual(WorkProjectSaveStatus.Saved, await fixture.Projects.TrySaveAsync(new(snapshot, 0,
            new(Guid.NewGuid(), digest, 1, Now), [], []), default));
        return id;
    }
    private static WorkUsageEntry UsageEntry(Fixture fixture, WorkProjectId project)
    {
        var id = new WorkUsageId(Guid.NewGuid()); var total = FlowRunTokenUsageAggregation.Invocation(20, 5, null);
        var record = new WorkUsageRecord(id, id, 1, WorkPlanId.New(), 1, new string('a', 64), new(Guid.NewGuid()), 1, new string('b', 64),
            new(WorkUsageSourceKind.FlowRun, "flow-run/1.0", Guid.NewGuid(), Guid.NewGuid(), "run-fixture", WorkPlanAttemptStatus.Completed),
            WorkUsageMeasurement.Measured, new(total, total), Now);
        return new(fixture.Workspace, fixture.Principal, project, record, WorkUsageFormat.Digest(record), Now);
    }
    private static WorkUsageEntry UsageCorrection(WorkUsageEntry original)
    {
        var record = original.Record with
        {
            Id = new(Guid.NewGuid()),
            Revision = 2,
            PreviousId = original.Record.Id,
            PreviousDigest = original.Digest,
            Measurement = WorkUsageMeasurement.Estimated,
            Correction = new(Guid.NewGuid(), new string('c', 64), "provider_correction", "receipt:fixture")
        };
        return original with { Record = record, Digest = WorkUsageFormat.Digest(record) };
    }
}
