using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task ReplayedCanonicalQueueHandoffYieldsOneExecution()
    {
        var gateway = new Agentstration.Runtime.Local.LocalWorkExecutionGateway(new Clock(Now));
        var id = WorkItemId.New(); var executionId = WorkExecutionId.New(); var workspace = WorkspaceId.New();
        var request = new WorkExecutionRequest(id, workspace, new("queue-fixture"), "flow", "Fixture", null, [], [],
            new Dictionary<string, string>(), new(new FlowId("fixture"))) { RequestedExecutionId = executionId };
        var first = await gateway.RequestExecutionAsync(request, default);
        var replay = await gateway.RequestExecutionAsync(request, default);
        Assert.AreEqual(first, replay);
        await gateway.ConfirmQueuedAsync(first, default);
        await gateway.ConfirmQueuedAsync(replay, default);
        using var cancellation = new CancellationTokenSource();
        await using var reader = gateway.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(executionId, reader.Current.Accepted.ExecutionId);
        var next = reader.MoveNextAsync().AsTask();
        Assert.IsFalse(next.IsCompleted, "A repeated queue handoff must not enqueue a second execution.");
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await next);
    }

    [TestMethod]
    public async Task ExecutionPersistsOneSuccessorAcrossRestartAndRequiresFunctionalAcceptance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        var start = Start(plan, tree);
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, start, default));
        Assert.HasCount(0, plan.Plan.Attempts);
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(1, plan.Plan.Attempts);
        var first = plan.Plan.Attempts.Single();
        Assert.AreEqual(tree.Tasks[1].Id, first.TaskId);
        Assert.AreEqual(1, first.TaskVersion);
        Assert.AreEqual(plan.Revision.Digest, first.PlanDigest);
        await harness.CompleteAsync(first, FlowRunStatus.Succeeded);
        // Both Work and Flow repositories are disposed before the terminal event is consumed.
        await fixture.ReopenAsync();
        harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(1, plan.Plan.Attempts);
        Assert.AreEqual(WorkPlanAttemptStatus.Completed, plan.Plan.Attempts[0].Status);
        Assert.AreEqual(WorkItemStatus.Completed, (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, first.WorkItemId, default))!.Value.Status);
        Assert.AreEqual(WorkTaskOutcome.Pending, State(plan, first.TaskId).Outcome);
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(plan, tree.Tasks[2].Id).Readiness);
        await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, first.TaskId,
            new(Guid.NewGuid(), State(plan, first.TaskId).Version, WorkTaskOutcome.Accepted, "Collected evidence reviewed"), default);
        await fixture.ReopenAsync();
        harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(2, plan.Plan.Attempts);
        var second = plan.Plan.Attempts.Single(item => item.TaskId == tree.Tasks[2].Id);
        Assert.AreEqual(first.TaskId, second.Dependencies.Single().TaskId);
        Assert.HasCount(1, await harness.FlowsAsync(second.TaskId));
        await harness.CompleteAsync(second, FlowRunStatus.Succeeded);
        plan = Success(await harness.AdvanceAsync(plan));
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(2, plan.Plan.Attempts);
        Assert.HasCount(1, await harness.FlowsAsync(second.TaskId));
        var final = await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, second.TaskId,
            new(Guid.NewGuid(), State(plan, second.TaskId).Version, WorkTaskOutcome.Accepted, "Summary matches evidence"), default);
        Assert.AreEqual(WorkTaskOutcome.Accepted, final.Value!.Tasks.Single(node => node.Definition.Id == tree.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual(plan.Plan.Version, Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, start, default)).Plan.Version);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GatewayFailureAfterWorkCreationResumesTheSameIntentAndExecution(bool afterQueuePersistence)
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        harness.Execution.FailRequest = !afterQueuePersistence;
        harness.Execution.FailConfirmation = afterQueuePersistence;
        await Assert.ThrowsAsync<IOException>(() => harness.AdvanceAsync(plan));
        var interrupted = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        var attempt = interrupted.Plan.Attempts.Single();
        Assert.AreEqual(WorkPlanAttemptStatus.Admitted, attempt.Status);
        Assert.AreEqual(afterQueuePersistence ? WorkItemStatus.Queued : WorkItemStatus.Pending,
            (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, attempt.WorkItemId, default))!.Value.Status);
        await fixture.ReopenAsync();
        harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(attempt.Id, plan.Plan.Attempts.Single().Id);
        Assert.AreEqual(attempt.WorkItemId, plan.Plan.Attempts.Single().WorkItemId);
        Assert.AreEqual(attempt.FlowRunId, (await harness.FlowsAsync(attempt.TaskId)).Single().Id);
        Assert.HasCount(1, harness.Execution.ExecutionIds);
    }

    [TestMethod]
    public async Task FailureAfterRootCreationReplaysOneRunAndConflictingCommandsFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        var start = Start(plan, tree);
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, start, default));
        Assert.AreEqual("work_plan_command_conflict", (await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id,
            start with { RevisionDigest = new string('f', 64) }, default)).ErrorCode);
        Assert.AreEqual("work_plan_conflict", (await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id,
            start with { CommandId = Guid.NewGuid() }, default)).ErrorCode);
        harness.FailAfterRoot = true;
        await Assert.ThrowsAsync<IOException>(() => harness.AdvanceAsync(plan));
        var stored = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(WorkPlanAttemptStatus.Admitted, stored.Plan.Attempts.Single().Status);
        await fixture.ReopenAsync();
        harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(1, plan.Plan.Attempts);
        Assert.HasCount(1, await harness.FlowsAsync(tree.Tasks[1].Id));
    }

    [TestMethod]
    public async Task FailedOrCancelledRunsBlockSuccessorsAndRequireExplicitRetry()
    {
        foreach (var terminal in new[] { FlowRunStatus.Failed, FlowRunStatus.Cancelled })
        {
            await using var fixture = await Fixture.CreateAsync();
            var tree = await fixture.CreateTreeAsync();
            var plan = await ActiveAsync(fixture, tree);
            var harness = new ExecutionHarness(fixture);
            await harness.InitializeAsync();
            plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
            plan = Success(await harness.AdvanceAsync(plan));
            var first = plan.Plan.Attempts.Single();
            await harness.CompleteAsync(first, terminal);
            plan = Success(await harness.AdvanceAsync(plan));
            plan = Success(await harness.AdvanceAsync(plan));
            Assert.HasCount(1, plan.Plan.Attempts);
            Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(plan, tree.Tasks[2].Id).Readiness);
            var failed = JsonSerializer.Serialize(plan.Plan.Attempts.Single());
            var retry = new ControlWorkPlanExecution(Guid.NewGuid(), plan.Plan.Version, first.TaskId);
            plan = Success(await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id, retry, default));
            Assert.AreEqual(plan.Plan.Version, Success(await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id, retry, default)).Plan.Version);
            plan = Success(await harness.AdvanceAsync(plan));
            Assert.HasCount(2, plan.Plan.Attempts);
            Assert.AreEqual(failed, JsonSerializer.Serialize(plan.Plan.Attempts[0]));
            Assert.AreNotEqual(first.FlowRunId, plan.Plan.Attempts[1].FlowRunId);
            Assert.AreEqual("work_plan_retry_not_allowed", (await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id,
                new(Guid.NewGuid(), plan.Plan.Version, first.TaskId), default)).ErrorCode);
        }
    }

    [TestMethod]
    public async Task CancellationIsDurableAndReplayedWithoutAdmittingSuccessors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        plan = Success(await harness.AdvanceAsync(plan));
        var cancel = new ControlWorkPlanExecution(Guid.NewGuid(), plan.Plan.Version);
        plan = Success(await harness.Service().CancelAsync(fixture.Workspace, plan.Plan.Id, cancel, default));
        await fixture.ReopenAsync();
        harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.IsFalse(plan.Plan.Execution!.Enabled);
        Assert.AreEqual(WorkPlanAttemptStatus.Cancelled, plan.Plan.Attempts.Single().Status);
        Assert.AreEqual(WorkItemStatus.Cancelled, (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, tree.Tasks[1].Id.ToWorkItemId(), default))!.Value.Status);
        Assert.AreEqual(plan.Plan.Version, Success(await harness.Service().CancelAsync(fixture.Workspace, plan.Plan.Id, cancel, default)).Plan.Version);
        Assert.HasCount(1, plan.Plan.Attempts);
    }

    [TestMethod]
    public async Task ManualTaskCancellationCannotDetachAnOpenAttemptFromItsPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        plan = Success(await harness.AdvanceAsync(plan));
        await fixture.SetStatusAsync(tree.Tasks[1].Id, WorkItemStatus.Cancelled);
        Assert.AreEqual("work_plan_tasks_active", (await fixture.Plans().TransitionAsync(fixture.Workspace, plan.Plan.Id,
            Phase(plan, WorkPlanAction.Deactivate), default)).ErrorCode);
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(WorkPlanAttemptStatus.Cancelled, plan.Plan.Attempts.Single().Status);
        Assert.IsTrue((await fixture.Plans().TransitionAsync(fixture.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Deactivate), default)).IsSuccess);
    }

    [TestMethod]
    public async Task ConcurrentSchedulersAndPlansCannotAdmitTheSameTaskTwice()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var other = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        other = Success(await harness.Service().StartAsync(fixture.Workspace, other.Plan.Id, Start(other, tree), default));
        await Task.WhenAll(harness.AdvanceAsync(plan), harness.AdvanceAsync(plan), harness.AdvanceAsync(other));
        var a = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        var b = Success(await fixture.Plans().GetAsync(fixture.Workspace, other.Plan.Id, default));
        Assert.AreEqual(1, a.Plan.Attempts.Count + b.Plan.Attempts.Count);
        Assert.HasCount(1, await harness.FlowsAsync(tree.Tasks[1].Id));
    }

    [TestMethod]
    public async Task AdmissionGateDeniesBeforeMutationAndRetainsExactEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var gate = new AdmissionGate();
        var harness = new ExecutionHarness(fixture) { Gates = [gate] };
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        Assert.AreEqual("repository_dependency_blocked", (await harness.AdvanceAsync(plan)).ErrorCode);
        Assert.HasCount(0, Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default)).Plan.Attempts);
        gate.Allowed = true;
        plan = Success(await harness.AdvanceAsync(plan));
        var captured = plan.Plan.Attempts.Single();
        Assert.AreEqual("revision-1", captured.AdmissionEvidence.Single().Revision);
        gate.Allowed = false;
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(JsonSerializer.Serialize(captured), JsonSerializer.Serialize(plan.Plan.Attempts.Single()));
    }

    [TestMethod]
    public async Task PlanExecutionRejectsForeignPrincipalsScopesAndStaleRevisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        var command = Start(plan, tree);
        Assert.AreEqual("work_plan_revision_conflict", (await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id,
            command with { Revision = 2 }, default)).ErrorCode);
        Assert.AreEqual("work_plan_not_found", (await harness.Service(Guid.NewGuid()).StartAsync(fixture.Workspace, plan.Plan.Id, command, default)).ErrorCode);
        Assert.AreEqual("work_plan_not_found", (await harness.Service().StartAsync(new(Guid.NewGuid()), plan.Plan.Id, command, default)).ErrorCode);
        Assert.AreEqual("work_plan_execution_targets_invalid", (await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id,
            command with { Targets = [command.Targets[0]] }, default)).ErrorCode);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, command, cancelled.Token));
        Assert.IsNull(Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default)).Plan.Execution);
    }

    [TestMethod]
    public async Task AdmissionAndPlanReceiptRollbackTogetherWhenTaskPersistenceFails()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var plan = await ActiveAsync(fixture, tree);
        var harness = new ExecutionHarness(fixture);
        await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree), default));
        await fixture.SqlAsync("CREATE TRIGGER FailAdmission BEFORE UPDATE ON WorkItems WHEN NEW.Status = 'Queued' BEGIN SELECT RAISE(ABORT, 'injected persistence failure'); END;");
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => harness.AdvanceAsync(plan));
        var stored = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(plan.Plan.Version, stored.Plan.Version);
        Assert.HasCount(0, stored.Plan.Attempts);
        Assert.AreEqual(1, State(stored, tree.Tasks[1].Id).Version);
        Assert.HasCount(0, harness.Execution.ExecutionIds);
        await fixture.SqlAsync("DROP TRIGGER FailAdmission;");
        Assert.HasCount(1, Success(await harness.AdvanceAsync(plan)).Plan.Attempts);
    }

    private static WorkPlanView Success(Result<WorkPlanView> result)
    { Assert.IsTrue(result.IsSuccess, result.ErrorCode); return result.Value!; }
    private static async Task<WorkPlanView> ActiveAsync(Fixture fixture, WorkTaskTreeDefinition tree)
    {
        var id = WorkPlanId.New();
        var view = Success(await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default));
        view = Success(await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(view, WorkPlanAction.Validate), default));
        return Success(await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(view, WorkPlanAction.Activate), default));
    }
    private static StartWorkPlanExecution Start(WorkPlanView view, WorkTaskTreeDefinition tree) => new(Guid.NewGuid(), view.Plan.Version,
        view.Revision.Number, view.Revision.Digest, tree.Tasks.Where(task => task.Kind == WorkTaskKind.Atomic)
            .Select(task => new WorkPlanTaskTarget(task.Id, new(new FlowId("deterministic")), JsonSerializer.SerializeToElement(new { instruction = task.Instruction }))).ToArray());

    private sealed class AdmissionGate : IWorkPlanAdmissionGate
    {
        public bool Allowed { get; set; }
        public Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan,
            WorkPlanRevision revision, WorkTaskId taskId, CancellationToken cancellationToken) => Task.FromResult(Allowed
                ? Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([new("repository", "fixture/issue/1", "revision-1", Now)])
                : Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Failure("repository_dependency_blocked"));
    }

    private sealed record ExecutionScope(FlowRunScope Current) : IWorkExecutionScopeAccessor;
    private sealed class ExecutionAuthorizer : IRootFlowSubmissionAuthorizer
    { public Task AuthorizeAsync(FlowRunScope scope, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class ExecutionTargetResolver : IRootFlowTargetResolver
    {
        public Task<ResolvedRootFlowTarget> ResolveAsync(FlowRunScope scope, FlowReference target, JsonElement input, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedRootFlowTarget(new(target.FlowId, "1.0.0", false, target.FlowId.Namespace)));
    }

    private sealed class PlanExecutionGateway : IWorkExecutionGateway
    {
        public bool FailRequest { get; set; }
        public bool FailConfirmation { get; set; }
        public HashSet<WorkExecutionId> ExecutionIds { get; } = [];
        public Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken)
        {
            if (FailRequest) throw new IOException("Injected gateway failure");
            var id = request.RequestedExecutionId ?? WorkExecutionId.New();
            lock (ExecutionIds) ExecutionIds.Add(id);
            return Task.FromResult(new WorkExecutionAccepted(id, null, Now, id.Value));
        }
        public Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken) => FailConfirmation
            ? throw new IOException("Injected queue confirmation failure") : Task.CompletedTask;
    }

    private sealed class ExecutionHarness(Fixture fixture) : IRootFlowRunGateway, IWorkPlanFlowObserver
    {
        private readonly SemaphoreSlim runLock = new(1);
        public bool FailAfterRoot { get; set; }
        public IReadOnlyList<IWorkPlanAdmissionGate> Gates { get; init; } = [];
        public IWorkProjectBindingResolver? Bindings { get; init; }
        public IWorkPlanRepository? PlanStore { get; init; }
        public PlanExecutionGateway Execution { get; } = new();
        private IFlowRepository FlowStore => fixture.FlowStore;
        private FlowRunScope Scope => new(fixture.Principal, fixture.Workspace, fixture.Principal);
        public Task InitializeAsync() => FlowStore.InitializeAsync(default);
        public WorkPlanExecutionService Service(Guid? owner = null)
        {
            var scopes = new[] { new ExecutionScope(Scope with { PrincipalId = owner ?? fixture.Principal }) };
            var work = new WorkItemService(fixture.Tasks, Execution, new Clock(Now), NullLogger<WorkItemService>.Instance, [], scopes);
            var target = new ExecutionTargetResolver(); var authorizer = new ExecutionAuthorizer();
            var root = new RootFlowSubmissionService(work, fixture.Tasks, target, this, authorizer, scopes);
            return new(PlanStore ?? fixture.Store, fixture.Tasks, fixture.Plans(owner: owner, store: PlanStore), ProjectContexts(fixture, Bindings, owner), work, root, target, authorizer, this,
                new Context(fixture.Workspace, owner ?? fixture.Principal), scopes, Gates, new Clock(Now), NullLogger<WorkPlanExecutionService>.Instance);
        }
        public Task<Result<WorkPlanView>> AdvanceAsync(WorkPlanView view) => Service().AdvanceAsync(fixture.Workspace, view.Plan.Id, default);
        public async Task<IReadOnlyList<FlowRun>> FlowsAsync(WorkTaskId taskId) =>
            (await FlowStore.ListRunsAsync(Scope, null, null, 0, 100, default)).Items.Select(item => item.Value).Where(run => run.WorkTaskId == taskId.ToString()).ToArray();
        public async Task CompleteAsync(WorkPlanAttempt attempt, FlowRunStatus status)
        {
            var stored = (await FlowStore.GetRunAsync(Scope, attempt.FlowRunId, default))!;
            await FlowStore.UpdateRunAsync(stored.Value with { Status = status, Output = JsonSerializer.SerializeToElement(new { evidence = "deterministic result" }),
                Error = status == FlowRunStatus.Failed ? new("fixture_failed", "Expected deterministic failure") : null }, stored.ETag, default);
        }
        public async Task<RootFlowRunResult> EnsureAsync(RootFlowRunRequest request, CancellationToken token)
        {
            await runLock.WaitAsync(token);
            try
            {
                var stored = await FlowStore.GetRunAsync(request.Scope, request.RunId, token);
                stored ??= await FlowStore.CreateRunAsync(new FlowRun
                {
                    Id = request.RunId, WorkspaceId = request.Scope.WorkspaceId, FlowId = request.Target.FlowId, FlowVersion = request.Target.Version!,
                    Scope = request.Scope, Input = request.Input, CreatedAt = Now, WorkItemResourceId = request.WorkItemId.ToString(), WorkTaskId = request.WorkTaskId,
                    DefinitionSnapshot = new(ResourceScopeRef.Workspace(fixture.Workspace.Value), request.Target.FlowId, request.Target.Version!, null,
                        new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "fixture")), new Dictionary<string, string>(), Now)
                }, token);
                if (FailAfterRoot) throw new IOException("Injected failure after root persistence");
                return new(stored.Value, stored.ETag);
            }
            finally { runLock.Release(); }
        }
        public async Task<FlowRun?> GetAsync(FlowRunScope scope, string runId, CancellationToken token) => (await FlowStore.GetRunAsync(scope, runId, token))?.Value;
        public async Task CancelAsync(FlowRunScope scope, string runId, CancellationToken token)
        {
            var stored = (await FlowStore.GetRunAsync(scope, runId, token))!;
            if (!stored.Value.Status.IsTerminal()) await FlowStore.UpdateRunAsync(stored.Value with { Status = FlowRunStatus.Cancelled }, stored.ETag, token);
        }
    }
}
