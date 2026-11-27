using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task DeliveryLedgerGuardsReviewedWorkAndRetainsOneImmutableIntentPerAttemptAcrossRestart()
    {
        await using var fixture = await Fixture.CreateAsync(); var operation = await DeliveryOperationAsync(fixture);
        await fixture.SqlAsync("DROP TABLE WorkTaskDeliveries;"); await fixture.Deliveries.InitializeAsync(default);
        var stale = operation with { Intent = operation.Intent with { PlanVersion = operation.Intent.PlanVersion + 1 } };
        stale = stale with { IntentDigest = WorkTaskDeliveryFormat.Digest(stale.Intent) };
        Assert.AreEqual(WorkTaskDeliverySaveStatus.WorkChanged, await fixture.Deliveries.AdmitAsync(stale, default));
        Assert.IsNull(await fixture.Deliveries.GetAttemptAsync(fixture.Workspace, fixture.Principal, operation.ProjectId, operation.Intent.Source.AttemptId, default));
        Assert.AreEqual(WorkTaskDeliverySaveStatus.Saved, await fixture.Deliveries.AdmitAsync(operation, default));
        Assert.AreEqual(WorkTaskDeliverySaveStatus.Replayed, await fixture.Deliveries.AdmitAsync(operation, default));
        Assert.AreEqual(WorkTaskDeliverySaveStatus.Conflict, await fixture.Deliveries.AdmitAsync(operation with { Id = new(Guid.NewGuid()) }, default));
        await fixture.ReopenAsync();
        var retained = await fixture.Deliveries.GetAsync(fixture.Workspace, fixture.Principal, operation.ProjectId, operation.Id, default);
        Assert.IsNotNull(retained); Assert.AreEqual(operation.IntentDigest, retained.IntentDigest);
        Assert.IsNull(await fixture.Deliveries.GetAsync(new(Guid.NewGuid()), fixture.Principal, operation.ProjectId, operation.Id, default));
        Assert.IsNull(await fixture.Deliveries.GetAsync(fixture.Workspace, Guid.NewGuid(), operation.ProjectId, operation.Id, default));
        Assert.IsNull(await fixture.Deliveries.GetAsync(fixture.Workspace, fixture.Principal, WorkProjectId.New(), operation.Id, default));
        Assert.HasCount(1, (await fixture.Deliveries.ListAsync(fixture.Workspace, fixture.Principal, operation.ProjectId, 0, 1, default)).Items);
    }
    [TestMethod]
    public async Task DeliveryLedgerCompareAndSwapRejectsIntentChangesAndPreservesSuccessfulEffects()
    {
        await using var fixture = await Fixture.CreateAsync(); var operation = await DeliveryOperationAsync(fixture); await fixture.Deliveries.InitializeAsync(default);
        Assert.AreEqual(WorkTaskDeliverySaveStatus.Saved, await fixture.Deliveries.AdmitAsync(operation, default));
        var changedIntent = operation.Intent with { Body = "Different reviewed delivery" };
        Assert.IsFalse(await fixture.Deliveries.UpdateAsync(operation with { Version = 2, Intent = changedIntent, IntentDigest = WorkTaskDeliveryFormat.Digest(changedIntent) }, 1, default));
        Assert.IsFalse(await fixture.Deliveries.UpdateAsync(operation with { Version = 2, Stage = WorkTaskDeliveryStage.Finished, Status = WorkTaskDeliveryStatus.Delivered }, 1, default));
        var next = operation with { Version = 2, Stage = WorkTaskDeliveryStage.CodePublication, EvidenceId = Guid.NewGuid() };
        var results = await Task.WhenAll(fixture.Deliveries.UpdateAsync(next, 1, default), fixture.Deliveries.UpdateAsync(next, 1, default));
        Assert.AreEqual(1, results.Count(value => value));
        var published = next with
        {
            Version = 3,
            Code = new(new string('c', 64), new string('d', 64), WorkTaskDeliveryEffectStatus.Succeeded,
            operation.Intent.HeadRef, operation.Intent.BaseRevision, new string('e', 40))
        };
        Assert.IsTrue(await fixture.Deliveries.UpdateAsync(published, 2, default));
        Assert.IsFalse(await fixture.Deliveries.UpdateAsync(published with { Version = 4, Code = published.Code! with { Revision = new string('f', 40) } }, 3, default));
        Assert.IsFalse(await fixture.Deliveries.UpdateAsync(published with { Version = 4, Stage = WorkTaskDeliveryStage.Prepared }, 3, default));
    }
    private static async Task<WorkTaskDeliveryOperation> DeliveryOperationAsync(Fixture fixture)
    {
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree); var remote = new SharingProvider();
        var (_, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        project = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), project.Version, project.Definition with { PlanIds = [plan.Plan.Id] }), default));
        var task = tree.Tasks.First(value => value.Kind == WorkTaskKind.Atomic).Id;
        var context = new WorkProjectContextSnapshot(project.Id, project.Version, new string('a', 64), [], Now) { Sharing = project.Definition.Sharing };
        var source = new WorkTaskDeliverySource(Guid.NewGuid(), WorkItemId.New(), "delivery-fixture", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('b', 64), WorkPlanAttemptStatus.Completed, null);
        var intent = new WorkTaskDeliveryIntent(plan.Plan.Id, plan.Plan.Version, plan.Revision.Number, plan.Revision.Digest, task, State(plan, task).Version,
            1, new string('c', 64), context, new("github", "fixture/repository", "1", "plan:", "participant:") { ConnectionName = "issues", Tracking = new("github", "42") }, 1, new string('d', 64), source,
            "fixture/repository", "github", "42", new Uri("https://github.com/fixture/repository.git"), new string('a', 40), "main", "refs/heads/agentstration/fixture", "Deliver fixture", "Reviewed fixture",
            [new("answer.txt", "NDIK", false)], [new("check", 0, true, null)]);
        var operation = new WorkTaskDeliveryOperation(new(Guid.NewGuid()), fixture.Workspace, fixture.Principal, project.Id, project.Version, intent,
            WorkTaskDeliveryFormat.Digest(intent), WorkTaskDeliveryStatus.Pending, WorkTaskDeliveryStage.Prepared, 1, Now, Now);
        Assert.IsTrue(WorkTaskDeliveryFormat.Valid(operation)); return operation;
    }
}
