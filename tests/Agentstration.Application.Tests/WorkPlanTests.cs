using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed partial class WorkPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 16, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SequentialReadinessRequiresActivationAndFunctionalAcceptanceAndSurvivesRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var create = Command(tree);
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default)).Value!;
        Assert.AreEqual(WorkPlanStatus.Draft, draft.Plan.Status);
        Assert.IsTrue(draft.Tasks.All(task => task.Readiness == WorkPlanTaskReadiness.Blocked));
        var validated = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(draft, WorkPlanAction.Validate), default)).Value!;
        var active = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(validated, WorkPlanAction.Activate), default)).Value!;
        Assert.AreEqual(WorkPlanTaskReadiness.Ready, State(active, tree.Tasks[1].Id).Readiness);
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(active, tree.Tasks[2].Id).Readiness);
        CollectionAssert.Contains(State(active, tree.Tasks[2].Id).Reasons.ToArray(), "dependencies_not_accepted");
        Assert.IsTrue(active.Tasks.All(task => task.Version == 1), "Planning must not change task versions.");
        var stored = await fixture.Tasks.ListTaskTreeAsync(fixture.Workspace, fixture.Principal, tree.RootTaskId, default);
        Assert.IsTrue(stored.All(task => task.Value.CurrentExecutionId is null && task.Value.Status == WorkItemStatus.Pending));
        await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, tree.Tasks[1].Id,
            new(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed collected evidence"), default);
        await fixture.ReopenAsync();
        var reloaded = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        Assert.AreEqual(WorkPlanStatus.Active, reloaded.Plan.Status);
        Assert.AreEqual(active.Plan.Version, reloaded.Plan.Version);
        Assert.AreEqual(WorkPlanTaskReadiness.Accepted, State(reloaded, tree.Tasks[1].Id).Readiness);
        Assert.AreEqual(WorkPlanTaskReadiness.Ready, State(reloaded, tree.Tasks[2].Id).Readiness);
        Assert.AreEqual(draft.Revision.Digest, reloaded.Revision.Digest);
        var receipt = (await fixture.Plans().GetReceiptAsync(fixture.Workspace, id, create.CommandId, default)).Value!;
        Assert.AreEqual(1, receipt.PlanVersion);
        Assert.IsTrue(receipt.ObservedTasks.All(task => task.Version == 1));
    }

    [TestMethod]
    public async Task InactiveRevisionsRetainExactOldEvidenceAndInvalidateValidation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var validated = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(draft, WorkPlanAction.Validate), default)).Value!;
        var active = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(validated, WorkPlanAction.Activate), default)).Value!;
        var revise = new ReviseWorkPlanCommand(Guid.NewGuid(), active.Plan.Version, draft.Revision.Definition with { Intent = "Revised intent" });
        Assert.AreEqual("work_plan_active", (await fixture.Plans().ReviseAsync(fixture.Workspace, id, revise, default)).ErrorCode);
        var inactive = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(active, WorkPlanAction.Deactivate), default)).Value!;
        revise = revise with { ExpectedVersion = inactive.Plan.Version };
        var revised = (await fixture.Plans().ReviseAsync(fixture.Workspace, id, revise, default)).Value!;
        Assert.AreEqual(2, revised.Revision.Number);
        Assert.AreEqual(WorkPlanStatus.Draft, revised.Plan.Status);
        Assert.IsNull(revised.Plan.ValidatedRevision);
        Assert.IsNull(revised.Plan.ValidatedDigest);
        Assert.AreEqual("work_plan_not_validated", (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(revised, WorkPlanAction.Activate), default)).ErrorCode);
        await fixture.ReopenAsync();
        var original = (await fixture.Plans().GetRevisionAsync(fixture.Workspace, id, 1, default)).Value!;
        Assert.AreEqual(JsonSerializer.Serialize(draft.Revision), JsonSerializer.Serialize(original));
        var replay = (await fixture.Plans().ReviseAsync(fixture.Workspace, id, revise, default)).Value!;
        Assert.AreEqual(revised.Plan.Version, replay.Plan.Version);
        Assert.AreEqual("work_plan_revision_not_found", (await fixture.Plans().GetRevisionAsync(fixture.Workspace, id, 3, default)).ErrorCode);
        Assert.AreEqual("work_plan_command_conflict", (await fixture.Plans().ReviseAsync(fixture.Workspace, id,
            revise with { Definition = revise.Definition with { Intent = "Different command intent" } }, default)).ErrorCode);
    }

    [TestMethod]
    public async Task CommandReplayPreservesVersionsAndRejectsChangedActionsOrIntents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var create = Command(tree);
        var first = (await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default)).Value!;
        var reordered = create with { Definition = create.Definition with { Intent = "  " + create.Definition.Intent + "  ", TaskIds = create.Definition.TaskIds.Reverse().ToArray() } };
        Assert.AreEqual(first.Plan.Version, (await fixture.Plans().CreateAsync(fixture.Workspace, id, reordered, default)).Value!.Plan.Version);
        var command = Phase(first, WorkPlanAction.Validate);
        var validated = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, command, default)).Value!;
        Assert.AreEqual(validated.Plan.Version, (await fixture.Plans().TransitionAsync(fixture.Workspace, id, command, default)).Value!.Plan.Version);
        Assert.AreEqual("work_plan_command_conflict", (await fixture.Plans().TransitionAsync(fixture.Workspace, id,
            command with { Action = WorkPlanAction.Activate }, default)).ErrorCode);
        Assert.AreEqual("work_plan_conflict", (await fixture.Plans().TransitionAsync(fixture.Workspace, id,
            command with { CommandId = Guid.NewGuid() }, default)).ErrorCode);
        Assert.AreEqual("work_plan_revision_conflict", (await fixture.Plans().TransitionAsync(fixture.Workspace, id,
            Phase(validated, WorkPlanAction.Activate) with { RevisionDigest = new string('a', 64) }, default)).ErrorCode);
        Assert.AreEqual("work_plan_revision_conflict", (await fixture.Plans().TransitionAsync(fixture.Workspace, id,
            Phase(validated, WorkPlanAction.Activate) with { Revision = 2 }, default)).ErrorCode);
    }

    [TestMethod]
    public async Task ForeignWorkspaceOrPrincipalCannotReferenceReadOrMutatePlans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var create = Command(tree);
        var own = (await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default)).Value!;
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var otherOwner = Guid.NewGuid();
        Assert.AreEqual("work_plan_not_found", (await fixture.Plans(otherWorkspace).GetAsync(fixture.Workspace, id, default)).ErrorCode);
        Assert.AreEqual("work_plan_not_found", (await fixture.Plans(otherWorkspace).GetAsync(otherWorkspace, id, default)).ErrorCode);
        Assert.AreEqual("work_plan_not_found", (await fixture.Plans(owner: otherOwner).GetAsync(fixture.Workspace, id, default)).ErrorCode);
        Assert.AreEqual("work_plan_revision_not_found", (await fixture.Plans(owner: otherOwner).GetRevisionAsync(fixture.Workspace, id, 1, default)).ErrorCode);
        Assert.AreEqual("work_plan_command_not_found", (await fixture.Plans(owner: otherOwner).GetReceiptAsync(fixture.Workspace, id, create.CommandId, default)).ErrorCode);
        Assert.AreEqual("work_plan_not_found", (await fixture.Plans(owner: otherOwner).TransitionAsync(fixture.Workspace, id, Phase(own, WorkPlanAction.Validate), default)).ErrorCode);
        Assert.AreEqual("work_plan_task_not_found", (await fixture.Plans(otherWorkspace).CreateAsync(otherWorkspace, WorkPlanId.New(), create, default)).ErrorCode);
        Assert.AreEqual("work_plan_task_not_found", (await fixture.Plans(owner: otherOwner).CreateAsync(fixture.Workspace, WorkPlanId.New(), create, default)).ErrorCode);
        Assert.IsNull(await fixture.Store.GetAsync(otherWorkspace, fixture.Principal, id, default));
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, otherOwner, id, 1, default));
    }

    [TestMethod]
    public async Task PortableIdentityAndRevisionDigestExcludeLocalScopeOwnerAndTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var otherOwner = Guid.NewGuid();
        await fixture.CreateTreeAsync(tree, otherWorkspace, otherOwner);
        var id = WorkPlanId.New();
        var a = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var b = (await fixture.Plans(otherWorkspace, otherOwner, clock: Now.AddDays(1)).CreateAsync(otherWorkspace, id, Command(tree), default)).Value!;
        Assert.AreEqual(a.Plan.Id, b.Plan.Id);
        Assert.AreEqual(a.Revision.Number, b.Revision.Number);
        Assert.AreEqual(a.Revision.Digest, b.Revision.Digest);
        Assert.AreNotEqual(a.Plan.WorkspaceId, b.Plan.WorkspaceId);
        Assert.AreNotEqual(a.Plan.OwnerPrincipalId, b.Plan.OwnerPrincipalId);
        Assert.AreNotEqual(a.Revision.CreatedAt, b.Revision.CreatedAt);
    }

    [TestMethod]
    public async Task CyclesAndForeignEdgesAreRejectedWithoutPersistingAPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var command = Command(tree);
        var cycle = command with { Definition = command.Definition with { Dependencies = [new(tree.Tasks[1].Id, tree.Tasks[2].Id), new(tree.Tasks[2].Id, tree.Tasks[1].Id)] } };
        Assert.AreEqual("work_plan_dependency_cycle", (await fixture.Plans().CreateAsync(fixture.Workspace, id, cycle, default)).ErrorCode);
        var foreign = command with { Definition = command.Definition with { Dependencies = [new(tree.Tasks[1].Id, new(Guid.NewGuid()))] } };
        Assert.AreEqual("work_plan_dependency_not_member", (await fixture.Plans().CreateAsync(fixture.Workspace, id, foreign, default)).ErrorCode);
        Assert.IsNull(await fixture.Store.GetAsync(fixture.Workspace, fixture.Principal, id, default));
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, fixture.Principal, id, 1, default));
        Assert.IsNull(await fixture.Store.GetReceiptAsync(fixture.Workspace, fixture.Principal, id, command.CommandId, default));
    }

    [TestMethod]
    public async Task CompositeMembershipRequiresItsRequiredChildrenAndRejectsCombinedCycles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var partial = new WorkPlanDefinition("Group checkpoint", [tree.RootTaskId, tree.Tasks[1].Id], []);
        Assert.AreEqual("work_plan_required_child_missing", (await fixture.Plans().CreateAsync(fixture.Workspace, WorkPlanId.New(),
            new(Guid.NewGuid(), 0, partial), default)).ErrorCode);
        var all = partial with { TaskIds = tree.Tasks.Select(task => task.Id).ToArray(), Dependencies = [new(tree.Tasks[1].Id, tree.RootTaskId)] };
        Assert.AreEqual("work_plan_hierarchy_dependency_cycle", (await fixture.Plans().CreateAsync(fixture.Workspace, WorkPlanId.New(),
            new(Guid.NewGuid(), 0, all), default)).ErrorCode);
        var optional = Tree(true);
        await fixture.CreateTreeAsync(optional);
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), 0, new("Optional child is not scheduled", [optional.RootTaskId, optional.Tasks[1].Id], [])), default)).Value!;
        var validated = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(draft, WorkPlanAction.Validate), default)).Value!;
        var active = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(validated, WorkPlanAction.Activate), default)).Value!;
        CollectionAssert.Contains(State(active, optional.RootTaskId).Reasons.ToArray(), "composite_outcome_derived");
        Assert.AreEqual(WorkPlanTaskReadiness.Ready, State(active, optional.Tasks[1].Id).Readiness);
        await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, optional.Tasks[1].Id, new(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed"), default);
        Assert.IsTrue((await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Tasks.All(task => task.Readiness == WorkPlanTaskReadiness.Accepted));
    }

    [TestMethod]
    [DataRow(WorkItemStatus.Queued)]
    [DataRow(WorkItemStatus.Running)]
    [DataRow(WorkItemStatus.WaitingForInput)]
    [DataRow(WorkItemStatus.WaitingForApproval)]
    [DataRow(WorkItemStatus.Paused)]
    public async Task RevisingCannotRemoveOrReplaceActiveWork(WorkItemStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        await fixture.SetStatusAsync(tree.Tasks[1].Id, status);
        var remove = new WorkPlanDefinition("Remove active member", [tree.Tasks[2].Id], []);
        Assert.AreEqual("work_plan_tasks_active", (await fixture.Plans().ReviseAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), draft.Plan.Version, remove), default)).ErrorCode);
        Assert.AreEqual(1, (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Plan.CurrentRevision);
    }

    [TestMethod]
    public async Task TaskStartingAfterPreflightIsFencedByTheStorageTransaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var hooked = new HookedPlans(fixture.Store, change => fixture.SetStatusAsync(tree.Tasks[1].Id, WorkItemStatus.Running));
        var result = await fixture.Plans(store: hooked).ReviseAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), draft.Plan.Version, draft.Revision.Definition with { Intent = "Raced revision" }), default);
        Assert.AreEqual("work_plan_tasks_active", result.ErrorCode);
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, fixture.Principal, id, 2, default));
        Assert.AreEqual(1, (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Plan.Version);
    }

    [TestMethod]
    public async Task TaskVersionChangeAfterPreflightRejectsTheWholePlanChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var hooked = new HookedPlans(fixture.Store, async change =>
        {
            var item = (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, tree.Tasks[1].Id.ToWorkItemId(), default))!.Value;
            var version = item.Version;
            item.AddMessage("Concurrent note", null, Guid.NewGuid(), Now);
            await fixture.Tasks.SaveAsync(item, version, default);
        });
        var result = await fixture.Plans(store: hooked).ReviseAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), draft.Plan.Version, draft.Revision.Definition), default);
        Assert.AreEqual("work_plan_task_changed", result.ErrorCode);
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, fixture.Principal, id, 2, default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentRevisionsHaveOneWinnerOrReplayOneReceipt(bool sameCommand)
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var gate = new TwoWriters();
        var hooked = new HookedPlans(fixture.Store, change => gate.EnterAsync());
        var command = new ReviseWorkPlanCommand(Guid.NewGuid(), draft.Plan.Version, draft.Revision.Definition with { Intent = "Revision two" });
        var other = sameCommand ? command : command with { CommandId = Guid.NewGuid(), Definition = command.Definition with { Intent = "Competing intent" } };
        var results = await Task.WhenAll(fixture.Plans(store: hooked).ReviseAsync(fixture.Workspace, id, command, default),
            fixture.Plans(store: hooked).ReviseAsync(fixture.Workspace, id, other, default));
        Assert.AreEqual(sameCommand ? 2 : 1, results.Count(result => result.IsSuccess));
        if (!sameCommand) Assert.AreEqual("work_plan_conflict", results.Single(result => !result.IsSuccess).ErrorCode);
        var current = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        Assert.AreEqual(2, current.Plan.Version);
        Assert.AreEqual(2, current.Revision.Number);
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, fixture.Principal, id, 3, default));
    }

    [TestMethod]
    public async Task WriteFailureRollsBackHeadRevisionReceiptAndGuardWitness()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        await fixture.SqlAsync("""
            CREATE TABLE PlanGuardWitness (Value INTEGER NOT NULL);
            INSERT INTO PlanGuardWitness VALUES (0);
            CREATE TRIGGER CountPlanGuard AFTER UPDATE ON WorkItems BEGIN UPDATE PlanGuardWitness SET Value = Value + 1; END;
            CREATE TRIGGER RejectPlanRevision BEFORE INSERT ON WorkPlanRevisions WHEN NEW.Number = 2
            BEGIN SELECT RAISE(ABORT, 'injected plan revision failure'); END;
            """);
        var command = new ReviseWorkPlanCommand(Guid.NewGuid(), draft.Plan.Version, draft.Revision.Definition with { Intent = "Must roll back" });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Plans().ReviseAsync(fixture.Workspace, id, command, default));
        StringAssert.Contains(failure.InnerException!.Message, "injected plan revision failure");
        Assert.AreEqual(1, (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Plan.Version);
        Assert.IsNull(await fixture.Store.GetRevisionAsync(fixture.Workspace, fixture.Principal, id, 2, default));
        Assert.IsNull(await fixture.Store.GetReceiptAsync(fixture.Workspace, fixture.Principal, id, command.CommandId, default));
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var counter = connection.CreateCommand();
        counter.CommandText = "SELECT Value FROM PlanGuardWitness;";
        Assert.AreEqual(0L, (long)(await counter.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task ExistingDatabaseUpgradeIsIdempotentAndKeepsTaskEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, tree.Tasks[1].Id, new(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Retained proof"), default);
        await fixture.SqlAsync("DROP TABLE WorkPlanCommands; DROP TABLE WorkPlanRevisions; DROP TABLE WorkPlans;");
        await fixture.Store.InitializeAsync(default);
        await fixture.Store.InitializeAsync(default);
        var proof = (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, tree.Tasks[1].Id.ToWorkItemId(), default))!.Value.TaskAcceptance!.Evidence!.Evidence;
        Assert.AreEqual("Retained proof", proof);
        Assert.IsTrue((await fixture.Plans().CreateAsync(fixture.Workspace, WorkPlanId.New(), Command(tree), default)).IsSuccess);
    }

    [TestMethod]
    public async Task RuntimeCompletionAndFailureNeverUnlockDependenciesWithoutAcceptance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var draft = (await fixture.Plans().CreateAsync(fixture.Workspace, id, Command(tree), default)).Value!;
        var valid = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(draft, WorkPlanAction.Validate), default)).Value!;
        var active = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(valid, WorkPlanAction.Activate), default)).Value!;
        await fixture.SetStatusAsync(tree.Tasks[1].Id, WorkItemStatus.Completed);
        var done = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        CollectionAssert.Contains(State(done, tree.Tasks[1].Id).Reasons.ToArray(), "task_review_required");
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(done, tree.Tasks[2].Id).Readiness);
        await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, tree.Tasks[1].Id,
            new(Guid.NewGuid(), State(done, tree.Tasks[1].Id).Version, WorkTaskOutcome.Failed, "Functional objective failed review"), default);
        var failed = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        CollectionAssert.Contains(State(failed, tree.Tasks[1].Id).Reasons.ToArray(), "task_outcome_terminal");
        Assert.AreEqual(WorkPlanTaskReadiness.Blocked, State(failed, tree.Tasks[2].Id).Readiness);
        Assert.AreEqual(active.Plan.Version, failed.Plan.Version);
    }

    [TestMethod]
    public async Task RevisionAndCommandHistoryAreBoundedWhileExistingReceiptsRemainReplayable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var create = Command(tree);
        var current = (await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default)).Value!;
        for (var revision = 2; revision <= WorkPlanDefinition.MaximumRevisions; revision++)
            current = (await fixture.Plans().ReviseAsync(fixture.Workspace, id,
                new(Guid.NewGuid(), current.Plan.Version, current.Revision.Definition), default)).Value!;
        Assert.AreEqual("work_plan_history_limit", (await fixture.Plans().ReviseAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), current.Plan.Version, current.Revision.Definition), default)).ErrorCode);
        while (current.Plan.Version < WorkPlanDefinition.MaximumCommands)
            current = (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(current, WorkPlanAction.Validate), default)).Value!;
        Assert.AreEqual("work_plan_history_limit", (await fixture.Plans().TransitionAsync(fixture.Workspace, id, Phase(current, WorkPlanAction.Activate), default)).ErrorCode);
        Assert.AreEqual(WorkPlanDefinition.MaximumCommands, (await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default)).Value!.Plan.Version);
    }

    [TestMethod]
    public async Task CancellationDoesNotPersistAPlanOrReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        var id = WorkPlanId.New();
        var command = Command(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Plans().CreateAsync(fixture.Workspace, id, command, cancellation.Token));
        Assert.IsNull(await fixture.Store.GetAsync(fixture.Workspace, fixture.Principal, id, default));
        Assert.IsNull(await fixture.Store.GetReceiptAsync(fixture.Workspace, fixture.Principal, id, command.CommandId, default));
    }

    [TestMethod]
    public void DefinitionValidationRejectsInvalidBoundsAndDuplicateMembershipOrEdges()
    {
        var tree = Tree();
        var definition = Command(tree).Definition;
        Assert.AreEqual("work_plan_definition_invalid", (definition with { Intent = " " }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_definition_invalid", (definition with { Intent = new string('x', WorkPlanDefinition.MaximumIntentLength + 1) }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_definition_invalid", (definition with { TaskIds = [] }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_definition_invalid", (definition with { TaskIds = Enumerable.Repeat(tree.RootTaskId, WorkPlanDefinition.MaximumTasks + 1).ToArray() }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_task_duplicate_or_invalid", (definition with { TaskIds = [tree.RootTaskId, tree.RootTaskId] }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_task_duplicate_or_invalid", (definition with { TaskIds = [new(Guid.Empty)] }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_dependency_duplicate", (definition with { Dependencies = [definition.Dependencies[0], definition.Dependencies[0]] }).Validate().ErrorCode);
        Assert.AreEqual("work_plan_definition_invalid", (definition with { Dependencies = Enumerable.Repeat(definition.Dependencies[0], WorkPlanDefinition.MaximumDependencies + 1).ToArray() }).Validate().ErrorCode);
    }

    [TestMethod]
    public async Task DraftValidationCanObserveActiveWorkButActivationCannotAdoptIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync();
        await fixture.SetStatusAsync(tree.Tasks[1].Id, WorkItemStatus.Running);
        var id = WorkPlanId.New();
        var create = Command(tree);
        var draft = await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default);
        Assert.IsTrue(draft.IsSuccess);
        var validation = Phase(draft.Value!, WorkPlanAction.Validate);
        var valid = await fixture.Plans().TransitionAsync(fixture.Workspace, id, validation, default);
        Assert.IsTrue(valid.IsSuccess);
        Assert.AreEqual("work_plan_tasks_active", (await fixture.Plans().TransitionAsync(fixture.Workspace, id,
            Phase(valid.Value!, WorkPlanAction.Activate), default)).ErrorCode);
        var replay = await fixture.Plans().CreateAsync(fixture.Workspace, id, create, default);
        Assert.IsTrue(replay.IsSuccess);
        Assert.AreEqual(valid.Value!.Plan.Version, replay.Value!.Plan.Version);
    }

    private static WorkTaskTreeDefinition Tree(bool optional = false)
    {
        var root = new WorkTaskId(Guid.NewGuid());
        return new(root, [new(root, WorkTaskKind.Composite, "Objective", "Deliver objective"),
            new(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Collect", "Collect evidence", root),
            new(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Summarize", "Summarize reviewed evidence", root, IsOptional: optional)]);
    }
    private static ReviseWorkPlanCommand Command(WorkTaskTreeDefinition tree) => new(Guid.NewGuid(), 0,
        new("Collect before summarizing", [tree.Tasks[1].Id, tree.Tasks[2].Id], [new(tree.Tasks[2].Id, tree.Tasks[1].Id)]));
    private static TransitionWorkPlanCommand Phase(WorkPlanView plan, WorkPlanAction action) => new(Guid.NewGuid(), plan.Plan.Version,
        plan.Revision.Number, plan.Revision.Digest, action);
    private static WorkPlanTaskState State(WorkPlanView plan, WorkTaskId id) => plan.Tasks.Single(task => task.TaskId == id);
    private sealed record Context(WorkspaceId WorkspaceId, Guid PrincipalId) : IWorkplaceContext;
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class TwoWriters
    {
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        public async Task EnterAsync()
        {
            if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private sealed class HookedPlans(IWorkPlanRepository inner, Func<WorkPlanChange, Task> beforeSave) : IWorkPlanRepository
    {
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<IReadOnlyList<WorkPlanSnapshot>> ListExecutionsAsync(int skip, int take, CancellationToken token) => inner.ListExecutionsAsync(skip, take, token);
        public Task<WorkPlanSnapshot?> GetAsync(WorkspaceId workspace, Guid owner, WorkPlanId id, CancellationToken token) => inner.GetAsync(workspace, owner, id, token);
        public Task<WorkPlanRevision?> GetRevisionAsync(WorkspaceId workspace, Guid owner, WorkPlanId id, long revision, CancellationToken token) => inner.GetRevisionAsync(workspace, owner, id, revision, token);
        public Task<WorkPlanCommandReceipt?> GetReceiptAsync(WorkspaceId workspace, Guid owner, WorkPlanId id, Guid command, CancellationToken token) => inner.GetReceiptAsync(workspace, owner, id, command, token);
        public async Task<WorkPlanSaveStatus> TrySaveAsync(WorkPlanChange change, CancellationToken token)
        { await beforeSave(change); return await inner.TrySaveAsync(change, token); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "agentstration-work-plan-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider? provider;
        public WorkspaceId Workspace { get; } = new(Guid.NewGuid());
        public Guid Principal { get; } = Guid.NewGuid();
        public string ConnectionString => $"Data Source={Path.Combine(directory, "work.db")};Pooling=False";
        public IWorkItemRepository Tasks => provider!.GetRequiredService<IWorkItemRepository>();
        public IWorkPlanRepository Store => provider!.GetRequiredService<IWorkPlanRepository>();
        public IWorkProjectRepository Projects => provider!.GetRequiredService<IWorkProjectRepository>();
        public IWorkProjectSharingRepository Sharing => provider!.GetRequiredService<IWorkProjectSharingRepository>();
        public IWorkTaskOperationRepository Operations => provider!.GetRequiredService<IWorkTaskOperationRepository>();
        public Agentstration.Flows.Storage.Abstractions.IFlowRepository FlowStore => provider!.GetRequiredService<Agentstration.Flows.Storage.Abstractions.IFlowRepository>();
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.directory); await fixture.ReopenAsync(); return fixture;
        }
        public WorkPlanService Plans(WorkspaceId? workspace = null, Guid? owner = null, IWorkPlanRepository? store = null, DateTimeOffset? clock = null) =>
            new(store ?? Store, Tasks, new Context(workspace ?? Workspace, owner ?? Principal), new Clock(clock ?? Now), NullLogger<WorkPlanService>.Instance);
        public WorkTaskTreeService Trees(WorkspaceId? workspace = null, Guid? owner = null) =>
            new(Tasks, new Context(workspace ?? Workspace, owner ?? Principal), new Clock(Now), NullLogger<WorkTaskTreeService>.Instance);
        public async Task<WorkTaskTreeDefinition> CreateTreeAsync(WorkTaskTreeDefinition? definition = null, WorkspaceId? workspace = null, Guid? owner = null)
        {
            definition ??= Tree();
            Assert.IsTrue((await Trees(workspace, owner).CreateAsync(workspace ?? Workspace, definition, default)).IsSuccess);
            return definition;
        }
        public async Task SetStatusAsync(WorkTaskId taskId, WorkItemStatus status)
        {
            var item = (await Tasks.GetAsync(Workspace, Principal, taskId.ToWorkItemId(), default))!.Value;
            await Tasks.SaveAsync(WorkItem.Restore(item.ToSnapshot() with { Status = status, Version = item.Version + 1 }), item.Version, default);
        }
        public async Task ReopenAsync()
        {
            if (provider is not null) await provider.DisposeAsync();
            var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(new Clock(Now)); services.AddSqliteWorkPlane(ConnectionString); services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")};Pooling=False"); provider = services.BuildServiceProvider();
            await Tasks.InitializeAsync(default); await Store.InitializeAsync(default);
        }
        public async Task SqlAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (provider is not null) await provider.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
