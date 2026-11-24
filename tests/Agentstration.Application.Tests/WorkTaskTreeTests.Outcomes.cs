using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agentstration.Application.Tests;

public sealed partial class WorkTaskTreeTests
{
    [TestMethod]
    public async Task RequiredChildrenConsolidateSeparatelyFromRuntimeAndSurviveRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var first = definition.Tasks[1].Id;
        var runtime = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, first.ToWorkItemId(), default))!;
        await fixture.Repository.SaveAsync(WorkItem.Restore(runtime.Value.ToSnapshot() with
        {
            Status = WorkItemStatus.Completed,
            Version = 2,
            Result = new([new("Runtime result")], [], new Dictionary<string, string>(), Now)
        }), 1, default);
        var initial = (await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!;
        Assert.IsNull(Node(initial, definition.RootTaskId).Acceptance);
        var partial = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, first,
            new(Guid.NewGuid(), 2, WorkTaskOutcome.Accepted, "  Reviewed first result  "), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Pending, Node(partial, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual("Reviewed first result", Node(partial, first).Acceptance!.Evidence!.Evidence);
        var complete = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[2].Id, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(complete, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual(WorkItemStatus.Pending, Node(complete, definition.RootTaskId).Status);
        Assert.HasCount(2, Node(complete, definition.RootTaskId).Acceptance!.RequiredChildren);
        foreach (var child in Node(complete, definition.RootTaskId).Acceptance!.RequiredChildren)
            Assert.AreEqual(Node(complete, child.TaskId).Version, child.Version);
        await fixture.ReopenAsync();
        var reloaded = (await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(reloaded, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual("Reviewed first result", Node(reloaded, first).Acceptance!.Evidence!.Evidence);
    }

    [TestMethod]
    [DataRow(WorkTaskOutcome.Failed)]
    [DataRow(WorkTaskOutcome.Cancelled)]
    public async Task FailedOrCancelledRequiredChildrenNeverConsolidateSuccess(WorkTaskOutcome outcome)
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, Accepted(), default);
        var result = await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[2].Id,
            new(Guid.NewGuid(), 1, outcome, "Required outcome evidence"), default);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(outcome, Node(result.Value!, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual(WorkItemStatus.Pending, Node(result.Value!, definition.Tasks[2].Id).Status);
    }

    [TestMethod]
    public async Task OptionalFailuresDoNotBlockRequiredSuccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        definition = definition with { Tasks = [definition.Tasks[0], definition.Tasks[1], definition.Tasks[2] with { IsOptional = true }] };
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var failed = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[2].Id,
            new(Guid.NewGuid(), 1, WorkTaskOutcome.Failed, "Optional result failed review"), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Pending, Node(failed, definition.RootTaskId).Acceptance!.Outcome);
        var accepted = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(accepted, definition.RootTaskId).Acceptance!.Outcome);
        Assert.HasCount(1, Node(accepted, definition.RootTaskId).Acceptance!.RequiredChildren);
        Assert.AreEqual(WorkTaskOutcome.Failed, Node(accepted, definition.Tasks[2].Id).Acceptance!.Outcome);
        var stored = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, definition.Tasks[2].Id.ToWorkItemId(), default))!;
        var changed = WorkItem.Restore(stored.Value.ToSnapshot() with
        { Version = stored.Value.Version + 1, TaskHierarchy = stored.Value.TaskHierarchy! with { IsOptional = false } });
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveAsync(changed, stored.Value.Version, default));
    }

    [TestMethod]
    public async Task EmptyAndOptionalOnlyCompositesRemainPendingAndCannotBeDirectlyAccepted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        definition = definition with { Tasks = [definition.Tasks[0], definition.Tasks[1] with { IsOptional = true }] };
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var result = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Pending, Node(result, definition.RootTaskId).Acceptance!.Outcome);
        Assert.HasCount(0, Node(result, definition.RootTaskId).Acceptance!.RequiredChildren);
        var empty = Tree();
        empty = empty with { Tasks = [empty.Tasks[0]] };
        await fixture.Service().CreateAsync(fixture.Workspace, empty, default);
        Assert.IsNull(Node((await fixture.Service().GetAsync(fixture.Workspace, empty.RootTaskId, default)).Value!, empty.RootTaskId).Acceptance);
        Assert.AreEqual("task_outcome_derived", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, empty.RootTaskId, Accepted(), default)).ErrorCode);
    }

    [TestMethod]
    public async Task NestedCompositeEvidenceTracksRequiredChildOutcomes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        var parent = definition.Tasks[1] with { Kind = WorkTaskKind.Composite };
        var leaf = new WorkTaskDefinition(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Nested", "Nested work", parent.Id);
        definition = definition with { Tasks = [definition.Tasks[0], parent, definition.Tasks[2], leaf] };
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var partial = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, leaf.Id, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(partial, parent.Id).Acceptance!.Outcome);
        Assert.AreEqual(WorkTaskOutcome.Pending, Node(partial, definition.RootTaskId).Acceptance!.Outcome);
        var result = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[2].Id, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(result, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual(Node(result, parent.Id).Version,
            Node(result, definition.RootTaskId).Acceptance!.RequiredChildren.Single(child => child.TaskId == parent.Id).Version);
    }

    [TestMethod]
    public async Task OutcomeReplayDoesNotAdvanceVersionsAndRejectsReusedEventsOrChangedEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var command = Accepted();
        var first = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, command, default)).Value!;
        await fixture.ReopenAsync();
        var replay = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id,
            command with { Evidence = "  " + command.Evidence + "  " }, default)).Value!;
        CollectionAssert.AreEqual(first.Tasks.Select(task => task.Version).ToArray(), replay.Tasks.Select(task => task.Version).ToArray());
        Assert.AreEqual("task_outcome_conflict", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id,
            command with { Evidence = "Different review" }, default)).ErrorCode);
        Assert.AreEqual("task_outcome_conflict", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[2].Id, command, default)).ErrorCode);
        Assert.AreEqual("task_outcome_conflict", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, Accepted(), default)).ErrorCode);
    }

    [TestMethod]
    public async Task StaleInvalidAndForeignOutcomesDoNotChangeTheFamily()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var child = definition.Tasks[1].Id;
        var stored = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, child.ToWorkItemId(), default))!;
        stored.Value.AddMessage("Note", null, Guid.NewGuid(), Now);
        await fixture.Repository.SaveAsync(stored.Value, 1, default);
        Assert.AreEqual("task_tree_conflict", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, child, Accepted(), default)).ErrorCode);
        foreach (var invalid in new[] { Accepted() with { EventId = Guid.Empty }, Accepted() with { ExpectedVersion = 0 },
            Accepted() with { Outcome = WorkTaskOutcome.Pending }, Accepted() with { Outcome = (WorkTaskOutcome)99 },
            Accepted() with { Evidence = " " }, Accepted() with { Evidence = new string('x', WorkTaskAcceptanceRules.MaximumEvidenceLength + 1) } })
            Assert.AreEqual("task_outcome_invalid", (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, child, invalid, default)).ErrorCode);
        Assert.AreEqual("work_task_not_found", (await fixture.Service(principal: Guid.NewGuid()).RecordOutcomeAsync(fixture.Workspace, child, Accepted(), default)).ErrorCode);
        var other = new Agentstration.Resources.WorkspaceId(Guid.NewGuid());
        Assert.AreEqual("work_task_not_found", (await fixture.Service(other).RecordOutcomeAsync(other, child, Accepted(), default)).ErrorCode);
        Assert.AreEqual("work_task_not_found", (await fixture.Service(other).RecordOutcomeAsync(fixture.Workspace, child, Accepted(), default)).ErrorCode);
        Assert.IsNull(Node((await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!, definition.RootTaskId).Acceptance);
    }

    [TestMethod]
    public async Task ConcurrentChildOutcomesRetryAndConsolidateWithoutLostUpdates()
    {
        var gate = new ConcurrentSaves();
        await using var fixture = await Fixture.CreateAsync(gate);
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var results = await Task.WhenAll(definition.Tasks.Skip(1).Select(child =>
            fixture.Service().RecordOutcomeAsync(fixture.Workspace, child.Id, Accepted(), default)));
        Assert.IsTrue(results.All(result => result.IsSuccess));
        var tree = (await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(tree, definition.RootTaskId).Acceptance!.Outcome);
        Assert.AreEqual(3, Node(tree, definition.RootTaskId).Version);
        Assert.IsTrue(gate.Attempts >= 3, "Both initial writes must race and one must retry.");
    }

    [TestMethod]
    public async Task ConcurrentDuplicateOutcomeCreatesOneEvidenceReceipt()
    {
        var gate = new ConcurrentSaves();
        await using var fixture = await Fixture.CreateAsync(gate);
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var command = Accepted();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, command, default)));
        Assert.IsTrue(results.All(result => result.IsSuccess));
        var tree = (await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!;
        Assert.AreEqual(2, Node(tree, definition.RootTaskId).Version);
        Assert.AreEqual(2, Node(tree, definition.Tasks[1].Id).Version);
        Assert.AreEqual(command.EventId, Node(tree, definition.Tasks[1].Id).Acceptance!.Evidence!.EventId);
    }

    [TestMethod]
    public async Task InjectedWriteFailureRollsBackChildAndParentOutcomes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE OutcomeWriteCounter (Value INTEGER NOT NULL);
                INSERT INTO OutcomeWriteCounter VALUES (0);
                CREATE TRIGGER CountOutcomeWrites AFTER UPDATE ON WorkItems
                BEGIN UPDATE OutcomeWriteCounter SET Value = Value + 1; END;
                CREATE TRIGGER RejectSecondOutcomeWrite BEFORE UPDATE ON WorkItems
                WHEN (SELECT Value FROM OutcomeWriteCounter) >= 1
                BEGIN SELECT RAISE(ABORT, 'injected second write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service().RecordOutcomeAsync(fixture.Workspace, definition.Tasks[1].Id, Accepted(), default));
        StringAssert.Contains(failure.InnerException!.Message, "injected second write failure");
        var tree = (await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!;
        Assert.IsTrue(tree.Tasks.All(task => task.Version == 1 && task.Acceptance is null));
        await using var verification = new SqliteConnection(fixture.ConnectionString);
        await verification.OpenAsync();
        await using var count = verification.CreateCommand();
        count.CommandText = "SELECT Value FROM OutcomeWriteCounter;";
        Assert.AreEqual(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task StaleRootOrMissingRootGuardRejectsTheWholeBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var items = await fixture.Repository.ListTaskTreeAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId, default);
        var root = items.Single(item => item.Value.Id == definition.RootTaskId.ToWorkItemId()).Value;
        var child = items.Single(item => item.Value.Id == definition.Tasks[1].Id.ToWorkItemId()).Value;
        child.RecordTaskOutcome(new(Guid.NewGuid(), WorkTaskOutcome.Accepted, "Review evidence", Now));
        root.ConsolidateTaskOutcome(items.Select(item => item.Value).Where(item => item.TaskHierarchy?.ParentTaskId == definition.RootTaskId).ToArray(), Now);
        var current = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, root.Id, default))!.Value;
        current.AddMessage("Concurrent root update", null, Guid.NewGuid(), Now);
        await fixture.Repository.SaveAsync(current, 1, default);
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveTaskTreeOutcomeAsync(
            fixture.Workspace, fixture.Principal, definition.RootTaskId, [new(child, 1), new(root, 1)], default));
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveTaskTreeOutcomeAsync(
            fixture.Workspace, fixture.Principal, definition.RootTaskId, [new(child, 1)], default));
        Assert.IsNull((await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, child.Id, default))!.Value.TaskAcceptance);
    }

    [TestMethod]
    public async Task DefaultRequiredChildrenKeepTheOriginalCreationDigest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree().Validate().Value!;
        var originalShape = new
        {
            definition.RootTaskId,
            Tasks = definition.Tasks.Select(task => new
            { task.Id, task.Kind, task.Title, task.Instruction, task.ParentTaskId, task.Description }).ToArray()
        };
        var originalDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(originalShape)));
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        var stored = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId.ToWorkItemId(), default))!;
        Assert.AreEqual(originalDigest, stored.Value.TaskHierarchy!.CreationDigest);
        var changed = definition with
        {
            Tasks = definition.Tasks.Select(task => task.Id == definition.RootTaskId
            ? task : task with { IsOptional = true }).ToArray()
        };
        Assert.AreEqual("task_tree_conflict", (await fixture.Service().CreateAsync(fixture.Workspace, changed, default)).ErrorCode);
    }

    [TestMethod]
    public async Task CancelledOutcomeRequestLeavesTheFamilyUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service().RecordOutcomeAsync(
            fixture.Workspace, definition.Tasks[1].Id, Accepted(), cancellation.Token));
        Assert.IsTrue((await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default)).Value!.Tasks.All(task => task.Version == 1));
    }

    private static RecordWorkTaskOutcome Accepted() => new(Guid.NewGuid(), 1, WorkTaskOutcome.Accepted, "Reviewed result evidence");
    [TestMethod]
    public async Task AtomicRootsAndMaximumDepthPathsPersistAcceptance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var atomic = new WorkTaskId(Guid.NewGuid());
        await fixture.Service().CreateAsync(fixture.Workspace,
            new(atomic, [new(atomic, WorkTaskKind.Atomic, "Single", "Single objective")]), default);
        var single = (await fixture.Service().RecordOutcomeAsync(fixture.Workspace, atomic, Accepted(), default)).Value!;
        Assert.AreEqual(WorkTaskOutcome.Accepted, Node(single, atomic).Acceptance!.Outcome);
        var root = new WorkTaskId(Guid.NewGuid());
        var nodes = new List<WorkTaskDefinition> { new(root, WorkTaskKind.Composite, "Root", "Root objective") };
        for (var depth = 1; depth <= WorkTaskTreeDefinition.MaximumDepth; depth++)
            nodes.Add(new(new(Guid.NewGuid()), depth == WorkTaskTreeDefinition.MaximumDepth ? WorkTaskKind.Atomic : WorkTaskKind.Composite,
                "Nested", "Nested objective", nodes[^1].Id));
        await fixture.Service().CreateAsync(fixture.Workspace, new(root, nodes), default);
        var deep = await fixture.Service().RecordOutcomeAsync(fixture.Workspace, nodes[^1].Id, Accepted(), default);
        Assert.IsTrue(deep.IsSuccess);
        Assert.IsTrue(deep.Value!.Tasks.All(task => task.Version == 2 && task.Acceptance?.Outcome == WorkTaskOutcome.Accepted));
    }

    private static WorkTaskTreeNode Node(WorkTaskTree tree, WorkTaskId id) => tree.Tasks.Single(task => task.Definition.Id == id);

    private sealed class ConcurrentSaves : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Modified))
            {
                var number = Interlocked.Increment(ref attempts);
                if (number == 2) bothEntered.TrySetResult();
                if (number <= 2) await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
