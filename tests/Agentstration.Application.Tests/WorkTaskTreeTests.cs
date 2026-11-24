using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed partial class WorkTaskTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CreationSurvivesRestartAndPreservesFunctionalIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        var created = await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        Assert.IsTrue(created.IsSuccess);
        Assert.HasCount(3, created.Value!.Tasks);
        Assert.IsTrue(created.Value.Tasks.All(task => task.Status == WorkItemStatus.Pending && task.CreatedAt == Now));

        await fixture.ReopenAsync();
        var loaded = await fixture.Service().GetAsync(fixture.Workspace, definition.RootTaskId, default);
        Assert.IsTrue(loaded.IsSuccess);
        CollectionAssert.AreEquivalent(definition.Tasks.Select(task => task.Id).ToArray(), loaded.Value!.Tasks.Select(task => task.Definition.Id).ToArray());
        Assert.AreEqual(2, loaded.Value.Tasks.Count(task => task.Definition.ParentTaskId == definition.RootTaskId));
        var projected = await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal, WorkplaceTasksOnly: true), default);
        Assert.HasCount(3, projected.Items);
        Assert.IsTrue(projected.Items.All(item => item.Value.CurrentExecutionId is null));
    }

    [TestMethod]
    public async Task ReplayIsIndependentOfInputOrderingAndRejectsChangedKind()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        definition = definition with
        {
            Tasks = definition.Tasks.Select(task => task with
            { Title = "  " + task.Title + "  ", Instruction = "  " + task.Instruction + "  ", Description = "  " }).ToArray()
        };
        var created = await fixture.Service().CreateAsync(fixture.Workspace, definition, default);
        Assert.IsTrue(created.IsSuccess);
        var reordered = definition with { Tasks = created.Value!.Tasks.Select(task => task.Definition).Reverse().ToArray() };
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, reordered, default)).IsSuccess);
        var changed = definition with
        {
            Tasks = definition.Tasks.Select(task => task.Id == definition.Tasks[1].Id
            ? task with { Kind = WorkTaskKind.Composite } : task).ToArray()
        };
        Assert.AreEqual("task_tree_conflict", (await fixture.Service().CreateAsync(fixture.Workspace, changed, default)).ErrorCode);
        Assert.HasCount(3, (await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal), default)).Items);
    }

    [TestMethod]
    public async Task ConcurrentReplayCreatesOneCompleteFamily()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.Service().CreateAsync(fixture.Workspace, definition, default)));
        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.HasCount(3, (await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal), default)).Items);
    }

    [TestMethod]
    public async Task ExistingChildCollisionRollsBackNewRootAndSiblings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        await fixture.Repository.CreateAsync(WorkItem.Create(definition.Tasks[1].Id.ToWorkItemId(), fixture.Workspace,
            fixture.Principal, "existing", "Existing work", Now), default);
        Assert.AreEqual("task_tree_conflict", (await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).ErrorCode);
        Assert.IsNull(await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId.ToWorkItemId(), default));
        Assert.HasCount(1, (await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal), default)).Items);
    }

    [TestMethod]
    public async Task WorkspaceAndPrincipalIsolationAlsoAppliesToTreeQueries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).IsSuccess);
        var foreignWorkspace = new WorkspaceId(Guid.NewGuid());
        var foreign = fixture.Service(foreignWorkspace);
        Assert.AreEqual("work_task_not_found", (await foreign.GetAsync(fixture.Workspace, definition.RootTaskId, default)).ErrorCode);
        Assert.AreEqual("work_task_not_found", (await foreign.GetAsync(foreignWorkspace, definition.RootTaskId, default)).ErrorCode);
        Assert.AreEqual("work_task_not_found", (await fixture.Service(principal: Guid.NewGuid()).GetAsync(fixture.Workspace, definition.RootTaskId, default)).ErrorCode);
        Assert.HasCount(0, await fixture.Repository.ListTaskTreeAsync(foreignWorkspace, fixture.Principal, definition.RootTaskId, default));
        Assert.HasCount(0, await fixture.Repository.ListTaskTreeAsync(fixture.Workspace, Guid.NewGuid(), definition.RootTaskId, default));
        Assert.IsTrue((await foreign.CreateAsync(foreignWorkspace, definition, default)).IsSuccess);
        Assert.HasCount(3, (await foreign.GetAsync(foreignWorkspace, definition.RootTaskId, default)).Value!.Tasks);
    }

    [TestMethod]
    public async Task ExternalParentReferenceCannotAdoptAnotherWorkspaceTask()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var other = Tree();
        Assert.IsTrue((await fixture.Service(otherWorkspace).CreateAsync(otherWorkspace, other, default)).IsSuccess);
        var definition = Tree();
        definition = definition with
        {
            Tasks = definition.Tasks.Select(task => task.Id == definition.Tasks[1].Id
            ? task with { ParentTaskId = other.RootTaskId } : task).ToArray()
        };
        Assert.AreEqual("task_parent_not_found", (await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).ErrorCode);
        Assert.HasCount(0, (await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal), default)).Items);
    }

    [TestMethod]
    public async Task SubtreeQueryExcludesSiblings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        var child = definition.Tasks[1];
        var grandchild = new WorkTaskDefinition(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Collect", "Collect evidence", child.Id);
        definition = definition with { Tasks = [definition.Tasks[0], child with { Kind = WorkTaskKind.Composite }, definition.Tasks[2], grandchild] };
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).IsSuccess);
        var subtree = await fixture.Service().GetAsync(fixture.Workspace, child.Id, default);
        Assert.IsTrue(subtree.IsSuccess);
        CollectionAssert.AreEquivalent(new[] { child.Id, grandchild.Id }, subtree.Value!.Tasks.Select(task => task.Definition.Id).ToArray());
    }

    [TestMethod]
    public async Task MembershipIsImmutableAndStaleSavesFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).IsSuccess);
        var stored = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId.ToWorkItemId(), default))!;
        var changed = WorkItem.Restore(stored.Value.ToSnapshot() with
        {
            Version = 2,
            TaskHierarchy = stored.Value.TaskHierarchy! with { Kind = WorkTaskKind.Atomic }
        });
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveAsync(changed, 1, default));
        stored.Value.AddMessage("A note", null, Guid.NewGuid(), Now.AddMinutes(1));
        await fixture.Repository.SaveAsync(stored.Value, 1, default);
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveAsync(stored.Value, 1, default));
    }

    [TestMethod]
    public async Task TerminalTreeNodeCannotBeDeletedLeavingOrphans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Tree();
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, definition, default)).IsSuccess);
        var item = (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId.ToWorkItemId(), default))!.Value;
        item.Cancel(null, Guid.NewGuid(), Now);
        var saved = await fixture.Repository.SaveAsync(item, 1, default);
        var error = await Assert.ThrowsAsync<WorkTransitionException>(() => fixture.Repository.DeleteTaskAsync(
            fixture.Workspace, fixture.Principal, definition.RootTaskId, saved.ETag, default));
        Assert.AreEqual("task_tree_delete_not_supported", error.Code);
        Assert.HasCount(3, (await fixture.Repository.ListTaskTreeAsync(fixture.Workspace, fixture.Principal, definition.RootTaskId, default)));
    }

    [TestMethod]
    public async Task ExistingDatabaseProjectionUpgradeIsIdempotentAndKeepsWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = WorkItem.Create(WorkItemId.New(), fixture.Workspace, fixture.Principal, "test", "Keep this", Now);
        await fixture.Repository.CreateAsync(legacy, default);
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX IX_WorkItems_WorkspaceId_OwnerPrincipalId_TaskTreeId; ALTER TABLE WorkItems DROP COLUMN TaskTreeId;";
            await command.ExecuteNonQueryAsync();
        }
        await fixture.Repository.InitializeAsync(default);
        await fixture.Repository.InitializeAsync(default);
        Assert.AreEqual("Keep this", (await fixture.Repository.GetAsync(fixture.Workspace, fixture.Principal, legacy.Id, default))!.Value.Instruction);
        Assert.IsTrue((await fixture.Service().CreateAsync(fixture.Workspace, Tree(), default)).IsSuccess);
    }

    [TestMethod]
    public async Task CancellationDoesNotPersistPartialWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service().CreateAsync(fixture.Workspace, Tree(), cancellation.Token));
        Assert.HasCount(0, (await fixture.Repository.QueryAsync(new(fixture.Workspace, fixture.Principal), default)).Items);
    }

    [TestMethod]
    public async Task LegacyContinuationsKeepOneFunctionalRootAndCurrentStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var metadata = new Dictionary<string, string>
        {
            [WorkplaceTaskIdentity.EntryMetadata] = "entry",
            [WorkplaceTaskIdentity.InteractionMetadata] = Guid.NewGuid().ToString()
        };
        var anchor = WorkItem.Create(WorkItemId.New(), fixture.Workspace, fixture.Principal, "entry", "Original goal", Now,
            title: "Original title", metadata: metadata);
        anchor = WorkItem.Restore(anchor.ToSnapshot() with { Status = WorkItemStatus.Completed });
        await fixture.Repository.CreateAsync(anchor, default);
        metadata[WorkplaceTaskIdentity.AnchorTaskMetadata] = anchor.Id.ToString();
        var continuation = WorkItem.Create(WorkItemId.New(), fixture.Workspace, fixture.Principal, "entry", "Follow-up", Now.AddMinutes(1), metadata: metadata);
        await fixture.Repository.CreateAsync(continuation, default);
        var root = await fixture.Service().GetAsync(fixture.Workspace, WorkTaskId.FromWorkItem(anchor.Id), default);
        Assert.IsTrue(root.IsSuccess);
        Assert.HasCount(1, root.Value!.Tasks);
        Assert.AreEqual("Original title", root.Value.Tasks[0].Definition.Title);
        Assert.AreEqual(WorkItemStatus.Pending, root.Value.Tasks[0].Status);
        Assert.AreEqual("work_task_not_found", (await fixture.Service().GetAsync(fixture.Workspace,
            WorkTaskId.FromWorkItem(continuation.Id), default)).ErrorCode);
    }

    [TestMethod]
    public void LegacySnapshotsAndTaskClassificationRetainTheirMeaning()
    {
        var workspace = new WorkspaceId(Guid.NewGuid());
        var item = WorkItem.Create(WorkItemId.New(), workspace, Guid.NewGuid(), "test", "Old task", Now,
            metadata: new Dictionary<string, string> { [WorkplaceTaskIdentity.EntryMetadata] = "entry", [WorkplaceTaskIdentity.InteractionMetadata] = Guid.NewGuid().ToString() });
        var json = JsonSerializer.SerializeToNode(item.ToSnapshot())!.AsObject();
        json.Remove(nameof(WorkItemSnapshot.TaskHierarchy));
        var legacy = WorkItem.Restore(json.Deserialize<WorkItemSnapshot>()!);
        Assert.IsNull(legacy.TaskHierarchy);
        Assert.IsTrue(WorkplaceTaskIdentity.IsTask(legacy));
        Assert.IsFalse(WorkplaceTaskIdentity.IsTask(WorkItem.Create(WorkItemId.New(), workspace, Guid.NewGuid(), "flow", "Technical child", Now)));
    }

    [TestMethod]
    public void ValidationRejectsCyclesAtomicParentsDuplicateIdsAndMissingRoots()
    {
        var definition = Tree();
        Assert.AreEqual("task_id_duplicate", (definition with { Tasks = [definition.Tasks[0], definition.Tasks[0]] }).Validate().ErrorCode);
        Assert.AreEqual("atomic_task_cannot_have_children", (definition with { Tasks = [definition.Tasks[0] with { Kind = WorkTaskKind.Atomic }, definition.Tasks[1]] }).Validate().ErrorCode);
        Assert.AreEqual("task_tree_root_invalid", (definition with { RootTaskId = new(Guid.NewGuid()) }).Validate().ErrorCode);
        Assert.AreEqual("task_tree_cycle", (definition with { Tasks = [definition.Tasks[0], definition.Tasks[1] with { Kind = WorkTaskKind.Composite, ParentTaskId = definition.Tasks[2].Id }, definition.Tasks[2] with { Kind = WorkTaskKind.Composite, ParentTaskId = definition.Tasks[1].Id }] }).Validate().ErrorCode);
        Assert.AreEqual("task_definition_invalid", (definition with { Tasks = [definition.Tasks[0] with { Kind = (WorkTaskKind)99 }] }).Validate().ErrorCode);
        Assert.AreEqual("task_tree_size_invalid", (definition with { Tasks = [] }).Validate().ErrorCode);
    }

    [TestMethod]
    public void DepthTaskCountAndTextBoundsAreEnforced()
    {
        var definition = Tree();
        var nodes = new List<WorkTaskDefinition> { definition.Tasks[0] };
        for (var depth = 0; depth <= WorkTaskTreeDefinition.MaximumDepth; depth++)
            nodes.Add(new(new(Guid.NewGuid()), WorkTaskKind.Composite, "Nested", "Work", nodes[^1].Id));
        Assert.AreEqual("task_tree_depth_exceeded", (definition with { Tasks = nodes }).Validate().ErrorCode);
        Assert.AreEqual("task_tree_size_invalid", (definition with { Tasks = Enumerable.Repeat(definition.Tasks[0], WorkTaskTreeDefinition.MaximumTasks + 1).ToArray() }).Validate().ErrorCode);
        var huge = Enumerable.Range(0, 11).Select(_ => new WorkTaskDefinition(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Task", new string('x', 100_000), definition.RootTaskId));
        Assert.AreEqual("task_tree_text_too_large", (definition with { Tasks = [definition.Tasks[0], .. huge] }).Validate().ErrorCode);
    }

    private static WorkTaskTreeDefinition Tree()
    {
        var root = new WorkTaskId(Guid.NewGuid());
        return new(root, [new(root, WorkTaskKind.Composite, "Assessment", "Assess the evidence"),
            new(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Collect", "Collect evidence", root),
            new(new(Guid.NewGuid()), WorkTaskKind.Atomic, "Summarize", "Summarize evidence", root)]);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Context(WorkspaceId WorkspaceId, Guid PrincipalId) : IWorkplaceContext;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "agentstration-task-tree-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider? provider;
        private IInterceptor? interceptor;
        public WorkspaceId Workspace { get; } = new(Guid.NewGuid());
        public Guid Principal { get; } = Guid.NewGuid();
        public string ConnectionString => $"Data Source={Path.Combine(directory, "work.db")};Pooling=False";
        public IWorkItemRepository Repository => provider!.GetRequiredService<IWorkItemRepository>();

        public static async Task<Fixture> CreateAsync(IInterceptor? interceptor = null)
        {
            var fixture = new Fixture { interceptor = interceptor };
            Directory.CreateDirectory(fixture.directory);
            await fixture.ReopenAsync();
            return fixture;
        }

        public WorkTaskTreeService Service(WorkspaceId? workspace = null, Guid? principal = null) => new(
            Repository, new Context(workspace ?? Workspace, principal ?? Principal), new FixedTime(), NullLogger<WorkTaskTreeService>.Instance);

        public async Task ReopenAsync()
        {
            if (provider is not null) await provider.DisposeAsync();
            var services = new ServiceCollection();
            services.AddSqliteWorkPlane(ConnectionString);
            if (interceptor is not null)
                services.AddSingleton<IDbContextFactory<WorkDbContext>>(new TestContextFactory(
                    new DbContextOptionsBuilder<WorkDbContext>().UseSqlite(ConnectionString).AddInterceptors(interceptor).Options));
            provider = services.BuildServiceProvider();
            await Repository.InitializeAsync(default);
        }

        public async ValueTask DisposeAsync()
        {
            if (provider is not null) await provider.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class TestContextFactory(DbContextOptions<WorkDbContext> options) : IDbContextFactory<WorkDbContext>
    {
        public WorkDbContext CreateDbContext() => new(options);
    }
}
