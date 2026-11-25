using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Work;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task UsageCaptureRequiresTheHistoricalProjectAndRetainsFailedAttemptsAndExplicitRetries()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider();
        var (_, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        project = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), project.Version, project.Definition with { PlanIds = [plan.Plan.Id] }), default));
        var harness = new ExecutionHarness(fixture); await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree) with { ProjectId = project.Id }, default));
        plan = Success(await harness.AdvanceAsync(plan)); var attempt = plan.Plan.Attempts.Single();
        var source = new UsageReader(); var usage = UsageService(fixture, remote, source); await usage.InitializeAsync(default);
        Assert.AreEqual("usage_execution_not_final", (await usage.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, attempt.Id, default)).ErrorCode);
        await harness.CompleteAsync(attempt, FlowRunStatus.Failed); plan = Success(await harness.AdvanceAsync(plan));
        var captured = await usage.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, attempt.Id, default);
        Assert.IsTrue(captured.IsSuccess, captured.ErrorCode); Assert.IsFalse(captured.Value!.Replayed);
        Assert.AreEqual(project.Definition.Sharing!.ProjectId, captured.Value.Items.Single().Record.ProjectId);
        Assert.AreEqual(WorkPlanAttemptStatus.Failed, captured.Value.Items.Single().Record.Source.Outcome);
        plan = Success(await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id,
            new(Guid.NewGuid(), plan.Plan.Version, attempt.TaskId), default));
        plan = Success(await harness.AdvanceAsync(plan)); var retry = plan.Plan.Attempts.Last();
        await harness.CompleteAsync(retry, FlowRunStatus.Cancelled); plan = Success(await harness.AdvanceAsync(plan));
        Assert.IsTrue((await usage.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, retry.Id, default)).IsSuccess);
        await fixture.ReopenAsync(); usage = UsageService(fixture, remote, source);
        Assert.IsTrue((await usage.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, attempt.Id, default)).Value!.Replayed);
        var summary = await usage.SummarizeAsync(fixture.Workspace, project.Id, new([plan.Plan.Id, plan.Plan.Id]), default);
        Assert.AreEqual(2, summary.Value!.OriginalRecords); Assert.AreEqual(60L, summary.Value.Measured.Inclusive.TotalTokens);
        Assert.AreEqual("work_project_not_found", (await UsageService(fixture, remote, source, Guid.NewGuid()).ListAsync(fixture.Workspace, project.Id, 0, 25, default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await usage.CaptureAsync(new(Guid.NewGuid()), project.Id, plan.Plan.Id, attempt.Id, default)).ErrorCode);
    }
    [TestMethod]
    public async Task UsagePublicationRecoversOneImmutableReceiptAndIndependentInstanceReadsTheSameCorrectedAccounting()
    {
        await using var left = await Fixture.CreateAsync(); await using var right = await Fixture.CreateAsync();
        var remote = new SharingProvider { LoseResponse = true }; var portable = new WorkSharingProjectId(Guid.NewGuid());
        var (sharing, lp) = await SharingAsync(left, remote, portable, new(Guid.NewGuid()));
        var (other, rp) = await SharingAsync(right, remote, portable, new(Guid.NewGuid()));
        var first = UsageEntry(left, lp.Id);
        first = first with { Record = first.Record with { ProjectId = portable, ParticipantId = lp.Definition.Sharing!.ParticipantId } };
        first = first with { Digest = WorkUsageFormat.Digest(first.Record) };
        await left.Usage.InitializeAsync(default); await left.Usage.CaptureAsync([first], default);
        var service = UsageService(left, remote, new UsageReader());
        var pending = await service.PublishAsync(left.Workspace, lp.Id, first.Record.Id, default);
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, pending.Value!.Status);
        await left.ReopenAsync(); service = UsageService(left, remote, new UsageReader());
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await service.PublishAsync(left.Workspace, lp.Id, first.Record.Id, default)).Value!.Status);
        Assert.AreEqual(1, remote.Publications);
        var total = FlowRunTokenUsageAggregation.Invocation(21, 6, 31);
        var command = new CorrectWorkUsageCommand(Guid.NewGuid(), first.Record.Id, first.Digest, WorkUsageMeasurement.Estimated, new(total, total), "provider_adjustment", "receipt:fixture");
        var correction = await service.CorrectAsync(left.Workspace, lp.Id, first.Record.RootId, command, default);
        Assert.IsTrue(correction.IsSuccess, correction.ErrorCode);
        Assert.AreEqual(correction.Value!.Digest, (await service.CorrectAsync(left.Workspace, lp.Id, first.Record.RootId, command, default)).Value!.Digest);
        Assert.AreEqual("usage_correction_conflict", (await service.CorrectAsync(left.Workspace, lp.Id, first.Record.RootId, command with { Reason = "changed" }, default)).ErrorCode);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await service.PublishAsync(left.Workspace, lp.Id, correction.Value.Record.Id, default)).Value!.Status);
        Assert.IsTrue((await other.PullAsync(right.Workspace, rp.Id, null, default)).IsSuccess);
        var local = (await service.SummarizeAsync(left.Workspace, lp.Id, new([]), default)).Value!;
        var shared = (await UsageService(right, remote, new UsageReader()).SummarizeAsync(right.Workspace, rp.Id, new([], Source: WorkUsageReadSource.Shared, ParticipantId: lp.Definition.Sharing!.ParticipantId), default)).Value!;
        Assert.AreEqual(31L, local.Estimated.Inclusive.TotalTokens); Assert.AreEqual(local.Estimated, shared.Estimated);
        Assert.AreEqual(1, shared.OriginalRecords); Assert.AreEqual(1, shared.Corrections); Assert.IsNull(shared.Measured.Inclusive.TotalTokens);
        Assert.HasCount(0, await right.Usage.ReadProjectAsync(right.Workspace, right.Principal, rp.Id, [], 10, default));
        var wire = System.Text.Json.JsonSerializer.Serialize((await other.PullAsync(right.Workspace, rp.Id, null, default)).Value!.Records);
        Assert.IsFalse(wire.Contains(left.Workspace.ToString(), StringComparison.Ordinal)); Assert.IsFalse(wire.Contains(left.Principal.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(2, remote.Publications);
        lp = ProjectSuccess(await ProjectService(left).UpdateAsync(left.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { Sharing = lp.Definition.Sharing! with { ParticipantId = new(Guid.NewGuid()) } }), default));
        Assert.AreEqual("usage_sharing_identity_changed", (await service.PublishAsync(left.Workspace, lp.Id, first.Record.Id, default)).ErrorCode);
    }
    private static WorkUsageService UsageService(Fixture fixture, SharingProvider remote, IWorkUsageExecutionReader reader, Guid? principal = null) => new(
        fixture.Usage, fixture.Projects, fixture.Store, fixture.Tasks, reader, fixture.Sharing, SharingService(fixture, remote, principal),
        new Context(fixture.Workspace, principal ?? fixture.Principal), new Clock(Now), NullLogger<WorkUsageService>.Instance);
    private sealed class UsageReader : IWorkUsageExecutionReader
    {
        public Task<Result<IReadOnlyList<WorkUsageExecution>>> ReadAsync(FlowRunScope scope, WorkProjectId projectId, WorkPlanId planId, WorkPlanAttempt attempt, CancellationToken cancellationToken)
        {
            var total = FlowRunTokenUsageAggregation.Invocation(20, 5, 30);
            return Task.FromResult(Result<IReadOnlyList<WorkUsageExecution>>.Success([new(new(WorkUsageSourceKind.FlowRun, "fixture/1.0", attempt.Id,
                attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Status), new(total, total), [])]));
        }
    }
}
