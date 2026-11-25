using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    private static WorkTaskExecutionEvidenceService Evidence(Fixture fixture, Guid? owner = null) => new(fixture.Store, fixture.Tasks,
        fixture.Workplace, new ExecutionHarness(fixture), new Context(fixture.Workspace, owner ?? fixture.Principal), new Clock(Now), NullLogger<WorkTaskExecutionEvidenceService>.Instance);

    [TestMethod]
    public async Task TaskAttemptHistoryRetainsBothReceiptsAndResultsAcrossRetryRestartAndUnrelatedRuns()
    {
        await using var fixture = await Fixture.CreateAsync(); var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture); await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        plan = Success(await harness.AdvanceAsync(plan)); var first = plan.Plan.Attempts.Single();
        await harness.CompleteAsync(first, FlowRunStatus.Failed); plan = Success(await harness.AdvanceAsync(plan));
        plan = Success(await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id, new(Guid.NewGuid(), plan.Plan.Version, first.TaskId), default));
        plan = Success(await harness.AdvanceAsync(plan)); var second = plan.Plan.Attempts.Last();
        await harness.CompleteAsync(second, FlowRunStatus.Succeeded); plan = Success(await harness.AdvanceAsync(plan));
        var scope = plan.Plan.Execution!.Scope;
        var run = (await fixture.FlowStore.GetRunAsync(scope, first.FlowRunId, default))!.Value;
        for (var index = 0; index < 211; index++) await fixture.FlowStore.CreateRunAsync(run with
        { Id = "unrelated-evidence-" + index, WorkTaskId = Guid.NewGuid().ToString("D"), CreatedAt = Now.AddMinutes(index + 1) }, default);
        await fixture.ReopenAsync();
        var page = await Evidence(fixture).ListAsync(fixture.Workspace, first.TaskId, 0, 1, default);
        Assert.IsTrue(page.IsSuccess, page.ErrorCode); Assert.AreEqual(2, page.Value!.TotalCount); Assert.IsTrue(page.Value.HasMore);
        var next = (await Evidence(fixture).ListAsync(fixture.Workspace, first.TaskId, 1, 1, default)).Value!;
        Assert.IsFalse(next.HasMore);
        var attempts = page.Value.Items.Concat(next.Items).Select(item => item.Attempt).ToArray();
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, attempts.Select(item => item.Id).ToArray());
        Assert.AreEqual("fixture_failed", attempts.Single(item => item.Id == first.Id).Error!.Code);
        Assert.IsNotNull(attempts.Single(item => item.Id == second.Id).Result);
        Assert.AreEqual(WorkTaskOutcome.Pending, State(plan, first.TaskId).Outcome);
        Assert.AreEqual("task_execution_evidence_stale", (await Evidence(fixture).RecordAsync(fixture.Workspace, plan.Plan.Id, first.TaskId,
            new(Guid.NewGuid(), first.Id, plan.Plan.Version, State(plan, first.TaskId).Version, first.TaskVersion, first.TaskDigest,
                "Obsolete attempt cannot attach to a newer retry", [], []), default)).ErrorCode);
        var count = await fixture.FlowStore.CountTaskRunsAsync(scope, first.TaskId.ToString(), default);
        Assert.AreEqual(2, count, "Reuse #129's task-indexed Flow history independently of global run volume.");
        Assert.IsFalse((await Evidence(fixture, Guid.NewGuid()).ListAsync(fixture.Workspace, first.TaskId, 0, 1, default)).IsSuccess);
        Assert.IsFalse((await Evidence(fixture).ListAsync(new(Guid.NewGuid()), first.TaskId, 0, 1, default)).IsSuccess);
        Assert.IsFalse((await Evidence(fixture).ListAsync(fixture.Workspace, first.TaskId, -1, 1, default)).IsSuccess);
        // Simulate upgrade from the pre-projection SQLite schema; persisted plan intent is the backfill source.
        await fixture.SqlAsync("DROP TABLE WorkTaskAttempts;"); await fixture.ReopenAsync();
        Assert.AreEqual(2, (await Evidence(fixture).ListAsync(fixture.Workspace, first.TaskId, 0, 100, default)).Value!.TotalCount);
    }

    [TestMethod]
    public async Task EvidencePinsTaskAndAttemptRejectsForeignReferencesAndReplaysWithoutFunctionalAcceptance()
    {
        await using var fixture = await Fixture.CreateAsync(); var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture); await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        plan = Success(await harness.AdvanceAsync(plan)); var attempt = plan.Plan.Attempts.Single();
        await harness.CompleteAsync(attempt, FlowRunStatus.Succeeded); plan = Success(await harness.AdvanceAsync(plan));
        var result = new WorkTaskResult(WorkTaskResultId.New(), fixture.Workspace, attempt.TaskId, attempt.FlowRunId, WorkTaskResultKind.Json,
            "Fixture", JsonSerializer.SerializeToElement(new { evidence = "fixture" }), Now);
        var artifact = new WorkTaskArtifact(WorkTaskArtifactId.New(), fixture.Workspace, attempt.TaskId, attempt.FlowRunId, "fixture.txt", "text/plain", 7, "fixture-only", Now);
        await fixture.Workplace.AddResultAsync(result, default); await fixture.Workplace.AddArtifactAsync(artifact, default);
        var rootRun = (await fixture.FlowStore.GetRunAsync(plan.Plan.Execution!.Scope, attempt.FlowRunId, default))!.Value;
        var childRun = rootRun with { Id = attempt.FlowRunId + "-child", RootFlowRunId = attempt.FlowRunId, ParentFlowRunId = attempt.FlowRunId };
        await fixture.FlowStore.CreateRunAsync(childRun, default);
        var childArtifact = artifact with { Id = WorkTaskArtifactId.New(), FlowRunId = childRun.Id };
        await fixture.Workplace.AddArtifactAsync(childArtifact, default);
        var command = new RecordWorkTaskExecutionEvidence(Guid.NewGuid(), attempt.Id, plan.Plan.Version, State(plan, attempt.TaskId).Version,
            attempt.TaskVersion, attempt.TaskDigest, "Observed and retained", [result.Id], [artifact.Id, childArtifact.Id]);
        var service = Evidence(fixture);
        Assert.AreEqual("task_execution_evidence_invalid", (await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId,
            command with { ArtifactIds = [artifact.Id, artifact.Id] }, default)).ErrorCode);
        Assert.AreEqual("task_execution_evidence_stale", (await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId,
            command with { AdmittedTaskVersion = attempt.TaskVersion + 1 }, default)).ErrorCode);
        var foreign = artifact with { Id = WorkTaskArtifactId.New(), WorkspaceId = new(Guid.NewGuid()) };
        await fixture.Workplace.AddArtifactAsync(foreign, default);
        Assert.AreEqual("task_execution_evidence_reference_invalid", (await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId,
            command with { ArtifactIds = [foreign.Id] }, default)).ErrorCode);
        Assert.IsFalse((await Evidence(fixture, Guid.NewGuid()).RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId, command, default)).IsSuccess);
        await fixture.SqlAsync("CREATE TRIGGER evidence_projection_failure BEFORE UPDATE ON WorkTaskAttempts BEGIN SELECT RAISE(ABORT, 'fixture projection failure'); END;");
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId, command, default));
        Assert.IsNull((await fixture.Store.GetAsync(fixture.Workspace, fixture.Principal, plan.Plan.Id, default))!.Attempts.Single().Evidence);
        await fixture.SqlAsync("DROP TRIGGER evidence_projection_failure;");
        var saved = await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId, command, default);
        Assert.IsTrue(saved.IsSuccess, saved.ErrorCode);
        Assert.AreEqual(command.EventId, saved.Value!.Attempt.Evidence!.EventId);
        await fixture.ReopenAsync(); service = Evidence(fixture);
        Assert.IsTrue((await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId, command, default)).IsSuccess);
        Assert.AreEqual("task_execution_evidence_conflict", (await service.RecordAsync(fixture.Workspace, plan.Plan.Id, attempt.TaskId,
            command with { Summary = "Changed evidence under the same event" }, default)).ErrorCode);
        var current = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(WorkTaskOutcome.Pending, State(current, attempt.TaskId).Outcome);
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(current, tree.Tasks[2].Id).Readiness);
        Assert.AreEqual(result.Id, (await service.ResultsAsync(fixture.Workspace, attempt.TaskId, 0, 1, default)).Value!.Items.Single().Id);
        var artifacts = (await service.ArtifactsAsync(fixture.Workspace, attempt.TaskId, 0, 1, default)).Value!;
        Assert.AreEqual(2, artifacts.TotalCount); Assert.IsTrue(artifacts.HasMore);
        var nextArtifacts = (await service.ArtifactsAsync(fixture.Workspace, attempt.TaskId, 1, 1, default)).Value!;
        CollectionAssert.AreEquivalent(new[] { artifact.Id, childArtifact.Id }, artifacts.Items.Concat(nextArtifacts.Items).Select(value => value.Id).ToArray());
        Assert.IsFalse((await service.ArtifactsAsync(fixture.Workspace, attempt.TaskId, 0, 101, default)).IsSuccess);
    }
}
