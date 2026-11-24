using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task ProjectAssociationsSurviveRestartWithoutChangingWorkOwnershipOrRevisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projects = ProjectService(fixture);
        await projects.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync();
        var first = await ActiveAsync(fixture, tree); var second = await ActiveAsync(fixture, tree);
        var id = WorkProjectId.New();
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, id, "operations", default,
            new(Guid.NewGuid(), 0, new("Operations", null, [], [])), default));
        Assert.HasCount(0, project.Definition.PlanIds);
        Assert.HasCount(0, project.Definition.TaskIds);
        project = ProjectSuccess(await projects.UpdateAsync(fixture.Workspace, id, new(Guid.NewGuid(), project.Version,
            project.Definition with { PlanIds = [first.Plan.Id, second.Plan.Id], TaskIds = [tree.RootTaskId] }), default));
        await fixture.ReopenAsync();
        projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var reopened = ProjectSuccess(await projects.GetAsync(fixture.Workspace, id, default));
        Assert.AreEqual(JsonSerializer.Serialize(project), JsonSerializer.Serialize(reopened));
        CollectionAssert.AreEquivalent(new[] { first.Plan.Id, second.Plan.Id }, reopened.Definition.PlanIds.ToArray());
        Assert.AreEqual(fixture.Principal, reopened.OwnerPrincipalId);
        Assert.AreEqual(fixture.Workspace, reopened.WorkspaceId);
        Assert.AreEqual(first.Plan.Version, Success(await fixture.Plans().GetAsync(fixture.Workspace, first.Plan.Id, default)).Plan.Version);
        Assert.AreEqual(fixture.Principal, (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, tree.RootTaskId.ToWorkItemId(), default))!.Value.OwnerPrincipalId);
        Assert.AreEqual(id, ProjectSuccess(await projects.FindAsync(fixture.Workspace, default, "operations", default)).Id);
    }

    [TestMethod]
    public async Task ProjectCommandsReplayAfterEditsAndRejectStaleOrChangedIntents()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var id = WorkProjectId.New(); var create = new SaveWorkProjectCommand(Guid.NewGuid(), 0, new(" Operations ", null, [], []));
        var project = ProjectSuccess(await service.CreateAsync(fixture.Workspace, id, "operations", default, create, default));
        var edit = new SaveWorkProjectCommand(Guid.NewGuid(), project.Version, project.Definition with { Description = "Local edits" });
        project = ProjectSuccess(await service.UpdateAsync(fixture.Workspace, id, edit, default));
        Assert.AreEqual(project.Version, ProjectSuccess(await service.UpdateAsync(fixture.Workspace, id, edit, default)).Version);
        var replay = ProjectSuccess(await service.CreateAsync(fixture.Workspace, id, "operations", default, create, default));
        Assert.AreEqual("Local edits", replay.Definition.Description);
        Assert.AreEqual("work_project_command_conflict", (await service.UpdateAsync(fixture.Workspace, id,
            edit with { Definition = edit.Definition with { DisplayName = "Changed intent" } }, default)).ErrorCode);
        Assert.AreEqual("work_project_conflict", (await service.UpdateAsync(fixture.Workspace, id, edit with { CommandId = Guid.NewGuid() }, default)).ErrorCode);
        Assert.AreEqual("work_project_conflict", (await service.CreateAsync(fixture.Workspace, WorkProjectId.New(), "operations", default, create, default)).ErrorCode);
        Assert.AreEqual("work_project_name_invalid", (await service.CreateAsync(fixture.Workspace, WorkProjectId.New(), "../escape", default, create, default)).ErrorCode);
    }

    [TestMethod]
    public async Task ProjectReadsReferencesAndListsRemainPrivateToWorkspaceAndPrincipal()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var id = WorkProjectId.New(); var command = new SaveWorkProjectCommand(Guid.NewGuid(), 0, new("Private", null, [plan.Plan.Id], [tree.Tasks[1].Id]));
        ProjectSuccess(await service.CreateAsync(fixture.Workspace, id, "private", default, command, default));
        var foreign = ProjectService(fixture, owner: Guid.NewGuid());
        Assert.AreEqual("work_project_not_found", (await foreign.GetAsync(fixture.Workspace, id, default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await foreign.FindAsync(fixture.Workspace, default, "private", default)).ErrorCode);
        Assert.HasCount(0, (await foreign.ListAsync(fixture.Workspace, 0, 10, default)).Value!.Items);
        Assert.AreEqual("work_project_work_not_found", (await foreign.CreateAsync(fixture.Workspace, WorkProjectId.New(), "foreign", default, command, default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await service.GetAsync(WorkspaceId.New(), id, default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await foreign.UpdateAsync(fixture.Workspace, id, command with { ExpectedVersion = 1 }, default)).ErrorCode);
        Assert.AreEqual("work_project_work_not_found", (await service.CreateAsync(fixture.Workspace, WorkProjectId.New(), "missing", default,
            command with { Definition = command.Definition with { PlanIds = [WorkPlanId.New()] } }, default)).ErrorCode);
        var foreignWorkspace = WorkspaceId.New();
        var foreignTree = await fixture.CreateTreeAsync(workspace: foreignWorkspace);
        var foreignPlan = Success(await fixture.Plans(foreignWorkspace).CreateAsync(foreignWorkspace, WorkPlanId.New(), Command(foreignTree), default));
        Assert.AreEqual("work_project_work_not_found", (await service.CreateAsync(fixture.Workspace, WorkProjectId.New(), "foreign-workspace", default,
            command with { Definition = command.Definition with { PlanIds = [foreignPlan.Plan.Id], TaskIds = [foreignTree.Tasks[1].Id] } }, default)).ErrorCode);
    }

    [TestMethod]
    public async Task ProjectListPagesAreBoundedDeterministicAndHaveExplicitHasMore()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        foreach (var name in new[] { "c", "b", "a" }) ProjectSuccess(await service.CreateAsync(fixture.Workspace, WorkProjectId.New(), name, default,
            new(Guid.NewGuid(), 0, new(name, null, [], [])), default));
        var first = (await service.ListAsync(fixture.Workspace, 0, 2, default)).Value!;
        CollectionAssert.AreEqual(new[] { "a", "b" }, first.Items.Select(project => project.Name).ToArray()); Assert.IsTrue(first.HasMore);
        var next = (await service.ListAsync(fixture.Workspace, 2, 2, default)).Value!;
        Assert.AreEqual("c", next.Items.Single().Name); Assert.IsFalse(next.HasMore);
        Assert.AreEqual("work_project_page_invalid", (await service.ListAsync(fixture.Workspace, -1, 2, default)).ErrorCode);
        Assert.AreEqual("work_project_page_invalid", (await service.ListAsync(fixture.Workspace, 0, 101, default)).ErrorCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentProjectEditsHaveOneWinnerAndObservedWorkChangesRollback(bool reuseCommand)
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var id = WorkProjectId.New();
        var project = ProjectSuccess(await service.CreateAsync(fixture.Workspace, id, "operations", default, new(Guid.NewGuid(), 0, new("Operations", null, [], [])), default));
        var barrier = new TwoWriters();
        var hooked = new HookedProjects(fixture.Projects, _ => barrier.EnterAsync());
        var command = new SaveWorkProjectCommand(Guid.NewGuid(), project.Version, project.Definition with { Description = "First" });
        var results = await Task.WhenAll(ProjectService(fixture, store: hooked).UpdateAsync(fixture.Workspace, id, command, default),
            ProjectService(fixture, store: hooked).UpdateAsync(fixture.Workspace, id, command with { CommandId = reuseCommand ? command.CommandId : Guid.NewGuid(), Definition = command.Definition with { Description = "Second" } }, default));
        Assert.AreEqual(1, results.Count(result => result.IsSuccess));
        Assert.AreEqual(reuseCommand ? "work_project_command_conflict" : "work_project_conflict", results.Single(result => !result.IsSuccess).ErrorCode);
        project = ProjectSuccess(await service.GetAsync(fixture.Workspace, id, default));
        var tree = await fixture.CreateTreeAsync(); var task = tree.Tasks[1].Id;
        var changed = new HookedProjects(fixture.Projects, _ => fixture.SetStatusAsync(task, WorkItemStatus.Running));
        Assert.AreEqual("work_project_work_changed", (await ProjectService(fixture, store: changed).UpdateAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), project.Version, project.Definition with { TaskIds = [task] }), default)).ErrorCode);
        Assert.HasCount(0, ProjectSuccess(await service.GetAsync(fixture.Workspace, id, default)).Definition.TaskIds);
        await fixture.SetStatusAsync(task, WorkItemStatus.Pending);
        var plan = await ActiveAsync(fixture, tree);
        var changedPlan = new HookedProjects(fixture.Projects, async _ =>
        {
            Assert.IsTrue((await fixture.Plans().TransitionAsync(fixture.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Deactivate), default)).IsSuccess);
        });
        Assert.AreEqual("work_project_work_changed", (await ProjectService(fixture, store: changedPlan).UpdateAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), project.Version, project.Definition with { PlanIds = [plan.Plan.Id] }), default)).ErrorCode);
    }

    [TestMethod]
    public async Task WorkspaceProjectBootstrapCreatesOnceAndPreservesLocalEditsAndIdentityPins()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var handler = new ProjectBootstrapResourceHandler(service, new Context(fixture.Workspace, fixture.Principal));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Agentstration.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var resource = ResourceManifestSerializer.FromYaml<BootstrapResourceDocument>(await File.ReadAllTextAsync(Path.Combine(root.FullName,
            "deploy", "bootstrap", "profiles", "workspace-project", "project.yaml")));
        var operation = new BootstrapResourceOperationContext("workspace-project", root.FullName, BootstrapProfileScope.Workspace,
            new(fixture.Principal, fixture.Workspace.Value)) { ActorPrincipalId = fixture.Principal };
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await handler.PlanAsync(resource, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(resource, operation, default));
        var created = ProjectSuccess(await service.FindAsync(fixture.Workspace, default, "operations", default));
        ProjectSuccess(await service.UpdateAsync(fixture.Workspace, created.Id, new(Guid.NewGuid(), created.Version, created.Definition with { DisplayName = "Locally edited" }), default));
        await fixture.ReopenAsync(); service = ProjectService(fixture); await service.InitializeAsync(default);
        handler = new(service, new Context(fixture.Workspace, fixture.Principal));
        Assert.AreEqual(BootstrapResourceDisposition.Skip, (await handler.PlanAsync(resource, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(resource, operation, default));
        var retained = ProjectSuccess(await service.FindAsync(fixture.Workspace, default, "operations", default));
        Assert.AreEqual(created.Id, retained.Id); Assert.AreEqual("Locally edited", retained.Definition.DisplayName);
        var conflict = resource with { Definition = JsonSerializer.SerializeToElement(new DeclarativeProjectDefinition(Guid.NewGuid())) };
        Assert.AreEqual(BootstrapResourceDisposition.Conflict, (await handler.PlanAsync(conflict, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Conflict, await handler.ApplyAsync(conflict, operation, default));
        Assert.IsFalse(((IBootstrapResourceHandler)handler).SupportsProfileScope(BootstrapProfileScope.Instance));
        await Assert.ThrowsAsync<WorkValidationException>(() => handler.ApplyAsync(resource, operation with { Target = new(fixture.Principal, Guid.NewGuid()) }, default));
    }

    [TestMethod]
    public async Task ProjectUpgradeAndFailedSavesDoNotMutateWorkOrCommandReceipts()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var id = WorkProjectId.New();
        var project = ProjectSuccess(await service.CreateAsync(fixture.Workspace, id, "operations", default,
            new(Guid.NewGuid(), 0, new("Operations", null, [], [])), default));
        await fixture.SqlAsync("CREATE TRIGGER FailProject BEFORE UPDATE ON WorkProjects BEGIN SELECT RAISE(ABORT, 'injected Project failure'); END;");
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => service.UpdateAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), project.Version, project.Definition with { PlanIds = [plan.Plan.Id] }), default));
        Assert.AreEqual(JsonSerializer.Serialize(project), JsonSerializer.Serialize(ProjectSuccess(await service.GetAsync(fixture.Workspace, id, default))));
        await fixture.SqlAsync("DROP TRIGGER FailProject; DROP TABLE WorkProjects;");
        await service.InitializeAsync(default); await service.InitializeAsync(default);
        Assert.AreEqual(plan.Plan.Version, Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default)).Plan.Version);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateAsync(fixture.Workspace, id, "operations", default,
            new(Guid.NewGuid(), 0, project.Definition), cancellation.Token));
        Assert.AreEqual("work_project_not_found", (await service.GetAsync(fixture.Workspace, id, default)).ErrorCode);
    }

    private static WorkProjectSnapshot ProjectSuccess(Result<WorkProjectSnapshot> result) { Assert.IsTrue(result.IsSuccess, result.ErrorCode); return result.Value!; }
    private static WorkProjectService ProjectService(Fixture fixture, Guid? owner = null, IWorkProjectRepository? store = null) =>
        new(store ?? fixture.Projects, fixture.Store, fixture.Tasks, new Context(fixture.Workspace, owner ?? fixture.Principal), new Clock(Now), NullLogger<WorkProjectService>.Instance);
    private sealed class HookedProjects(IWorkProjectRepository inner, Func<WorkProjectChange, Task> beforeSave) : IWorkProjectRepository
    {
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<WorkProjectSnapshot?> GetAsync(WorkspaceId workspace, Guid owner, WorkProjectId id, CancellationToken token) => inner.GetAsync(workspace, owner, id, token);
        public Task<WorkProjectSnapshot?> FindAsync(WorkspaceId workspace, Guid owner, ResourceNamespace resourceNamespace, string name, CancellationToken token) => inner.FindAsync(workspace, owner, resourceNamespace, name, token);
        public Task<WorkProjectPage> ListAsync(WorkspaceId workspace, Guid owner, int skip, int take, CancellationToken token) => inner.ListAsync(workspace, owner, skip, take, token);
        public async Task<WorkProjectSaveStatus> TrySaveAsync(WorkProjectChange change, CancellationToken token) { await beforeSave(change); return await inner.TrySaveAsync(change, token); }
    }
}
