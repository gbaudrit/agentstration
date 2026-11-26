using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActiveAttemptsKeepProjectV1WhileNewAdmissionsCaptureV2AndRevocationDoesNotRewriteEvidence(bool contracts)
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var bindings = new ProjectBindings { Evidence = ContractEvidence }; var projects = ProjectService(fixture, bindings: bindings); await projects.InitializeAsync(default);
        var initialBinding = contracts ? ContractBinding(fixture) : Binding(fixture);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [plan.Plan.Id], []) { Bindings = [initialBinding] }), default));
        var harness = new ExecutionHarness(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree) with { ProjectId = project.Id }, default));
        plan = Success(await harness.AdvanceAsync(plan));
        var first = plan.Plan.Attempts.Single(); var original = JsonSerializer.Serialize(first.ProjectContext);
        Assert.AreEqual(1, first.ProjectContext!.ProjectVersion);
        Assert.AreEqual(1, first.ProjectContext.Bindings.Single().Binding.Target.Generation);
        if (contracts) Assert.IsNotNull(first.ProjectContext.Bindings.Single().Contract);
        var work = (await fixture.Tasks.GetAsync(fixture.Workspace, fixture.Principal, first.WorkItemId, default))!.Value;
        Assert.AreEqual(first.ProjectContext.Digest, work.Metadata["workProject.contextDigest"]);
        bindings.RequiredGeneration = 2;
        Assert.AreEqual("work_project_binding_stale", (await ProjectContexts(fixture, bindings).GetAvailabilityAsync(fixture.Workspace, project.Id, default)).Value!.Bindings.Single().Code);
        Assert.IsTrue((await harness.AdvanceAsync(plan)).IsSuccess, "A changed resource does not retarget or readmit an active attempt.");
        var target = project.Definition.Bindings.Single();
        project = ProjectSuccess(await projects.UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { Bindings = [target with { Target = target.Target with { Generation = 2 } }] }), default));
        await fixture.ReopenAsync();
        harness = new(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(original, JsonSerializer.Serialize(plan.Plan.Attempts.Single().ProjectContext));
        await harness.CompleteAsync(first, Agentstration.Flows.FlowRunStatus.Succeeded);
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.IsTrue((await fixture.Trees().RecordOutcomeAsync(fixture.Workspace, first.TaskId,
            new(Guid.NewGuid(), State(plan, first.TaskId).Version, WorkTaskOutcome.Accepted, "Collected evidence accepted"), default)).IsSuccess);
        plan = Success(await harness.AdvanceAsync(plan));
        var second = plan.Plan.Attempts.Single(attempt => attempt.TaskId == tree.Tasks[2].Id);
        Assert.AreEqual(2, second.ProjectContext!.ProjectVersion);
        Assert.AreEqual(2, second.ProjectContext.Bindings.Single().Binding.Target.Generation);
        Assert.AreNotEqual(first.ProjectContext.Digest, second.ProjectContext.Digest);
        if (contracts)
        {
            Assert.AreNotEqual(first.ProjectContext.Bindings.Single().Contract!.MappingDigest, second.ProjectContext.Bindings.Single().Contract!.MappingDigest);
            Assert.AreEqual("work_project_binding_stale", (await ProjectContexts(fixture, bindings).RevalidateAsync(fixture.Workspace, first.ProjectContext.Bindings.Single(), default)).ErrorCode);
        }
        bindings.Failure = "work_project_binding_denied";
        var available = (await ProjectContexts(fixture, bindings).GetAvailabilityAsync(fixture.Workspace, project.Id, default)).Value!;
        Assert.IsFalse(available.Bindings.Single().Available);
        Assert.AreEqual("work_project_binding_denied", available.Bindings.Single().Code);
        var saved = JsonSerializer.Serialize(plan.Plan.Attempts.Select(attempt => attempt.ProjectContext));
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(saved, JsonSerializer.Serialize(plan.Plan.Attempts.Select(attempt => attempt.ProjectContext)));
    }

    [TestMethod]
    public async Task RevokedBindingsBlockNewAdmissionWithoutChangingPlanOrTask()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var bindings = new ProjectBindings(); var projects = ProjectService(fixture, bindings: bindings); await projects.InitializeAsync(default);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [plan.Plan.Id], []) { Bindings = [Binding(fixture)] }), default));
        var harness = new ExecutionHarness(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree) with { ProjectId = project.Id }, default));
        bindings.Failure = "work_project_binding_denied";
        Assert.AreEqual("work_project_binding_denied", (await harness.AdvanceAsync(plan)).ErrorCode);
        var unchanged = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(plan.Plan.Version, unchanged.Plan.Version); Assert.HasCount(0, unchanged.Plan.Attempts);
        Assert.AreEqual(1, State(unchanged, tree.Tasks[1].Id).Version);
        Assert.HasCount(0, harness.Execution.ExecutionIds);
        bindings.Failure = null;
        Assert.HasCount(1, Success(await harness.AdvanceAsync(plan)).Plan.Attempts);
    }

    [TestMethod]
    public async Task ExplicitRetryCapturesCurrentProjectContextAndRetainsTheFailedAttempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var bindings = new ProjectBindings(); var projects = ProjectService(fixture, bindings: bindings); await projects.InitializeAsync(default);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [plan.Plan.Id], []) { Bindings = [Binding(fixture)] }), default));
        var harness = new ExecutionHarness(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree) with { ProjectId = project.Id }, default));
        plan = Success(await harness.AdvanceAsync(plan)); var first = plan.Plan.Attempts.Single();
        await harness.CompleteAsync(first, Agentstration.Flows.FlowRunStatus.Failed);
        plan = Success(await harness.AdvanceAsync(plan));
        var retained = JsonSerializer.Serialize(plan.Plan.Attempts.Single());
        project = ProjectSuccess(await projects.UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), project.Version, project.Definition with { Description = "Explicit retry context v2" }), default));
        plan = Success(await harness.Service().RetryAsync(fixture.Workspace, plan.Plan.Id,
            new(Guid.NewGuid(), plan.Plan.Version, first.TaskId), default));
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.HasCount(2, plan.Plan.Attempts);
        Assert.AreEqual(retained, JsonSerializer.Serialize(plan.Plan.Attempts[0]));
        Assert.AreEqual(project.Version, plan.Plan.Attempts[1].ProjectContext!.ProjectVersion);
        Assert.AreEqual(1, plan.Plan.Attempts[0].ProjectContext!.ProjectVersion);
    }

    [TestMethod]
    public async Task ConcurrentProjectEditRejectsAdmissionAndRollsBackTheTaskClaim()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [plan.Plan.Id], []) { Bindings = [Binding(fixture)] }), default));
        var edited = false;
        var store = new HookedPlans(fixture.Store, async change =>
        {
            if (change.Project is null || edited) return;
            edited = true;
            project = ProjectSuccess(await projects.UpdateAsync(fixture.Workspace, project.Id,
                new(Guid.NewGuid(), project.Version, project.Definition with { Description = "Explicit edit during admission" }), default));
        });
        var harness = new ExecutionHarness(fixture) { PlanStore = store }; await harness.InitializeAsync();
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, Start(plan, tree) with { ProjectId = project.Id }, default));
        Assert.AreEqual("work_project_context_changed", (await harness.AdvanceAsync(plan)).ErrorCode);
        var unchanged = Success(await fixture.Plans().GetAsync(fixture.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(plan.Plan.Version, unchanged.Plan.Version); Assert.HasCount(0, unchanged.Plan.Attempts);
        Assert.AreEqual(1, State(unchanged, tree.Tasks[1].Id).Version);
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(project.Version, plan.Plan.Attempts.Single().ProjectContext!.ProjectVersion);
    }

    [TestMethod]
    public async Task ContextRequiresPrivateAssociatedProjectAndExactValidReferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "context", default,
            new(Guid.NewGuid(), 0, new("Context", null, [], []) { Bindings = [Binding(fixture)] }), default));
        Assert.AreEqual("work_project_not_found", (await ProjectContexts(fixture, owner: Guid.NewGuid()).GetAvailabilityAsync(fixture.Workspace, project.Id, default)).ErrorCode);
        Assert.AreEqual("work_project_plan_not_associated", (await ProjectContexts(fixture).CaptureAsync(fixture.Workspace, project.Id, WorkPlanId.New(), default)).ErrorCode);
        var binding = Binding(fixture);
        foreach (var invalid in new[]
        {
            new[] { binding, binding },
            new[] { binding, binding with { Name = "other" } },
            new[] { binding with { Target = binding.Target with { Uid = Guid.Empty } } },
            new[] { binding with { Target = binding.Target with { Generation = 0 } } },
            new[] { binding with { Target = binding.Target with { Address = binding.Target.Address with { Kind = "Secret" } } } },
            new[] { binding with { Target = binding.Target with { Address = binding.Target.Address with { ScopeRef = default } } } },
            Enumerable.Range(0, 33).Select(index => binding with { Name = "binding-" + index, Purpose = WorkProjectBindingPurpose.Context }).ToArray()
        }) Assert.IsFalse((project.Definition with { Bindings = invalid }).Validate().IsSuccess);
    }

    [TestMethod]
    public async Task LegacyProjectCommandDigestsRemainReplayableAfterTheBindingSchemaUpgrade()
    {
        await using var fixture = await Fixture.CreateAsync(); var service = ProjectService(fixture); await service.InitializeAsync(default);
        var id = WorkProjectId.New(); var commandId = Guid.NewGuid();
        var legacy = new { DisplayName = "Operations", Description = (string?)null, PlanIds = Array.Empty<WorkPlanId>(), TaskIds = Array.Empty<WorkTaskId>() };
        var digest = WorkPlanDefinition.Digest(new { Action = "create", Name = "operations", Namespace = ResourceNamespace.Default, Definition = legacy });
        var receipt = new WorkProjectCommandReceipt(commandId, digest, 1, Now);
        var snapshot = new WorkProjectSnapshot(id, fixture.Workspace, fixture.Principal, "operations", default,
            new("Operations", null, [], []), 1, digest, Now, Now, [receipt]);
        Assert.AreEqual(WorkProjectSaveStatus.Saved, await fixture.Projects.TrySaveAsync(new(snapshot, 0, receipt, [], []), default));
        await fixture.SqlAsync("UPDATE WorkProjects SET Payload = json_remove(Payload, '$.definition.bindings');");
        await fixture.ReopenAsync();
        var replay = ProjectSuccess(await ProjectService(fixture).CreateAsync(fixture.Workspace, id, "operations", default,
            new(commandId, 0, new("Operations", null, [], [])), default));
        Assert.AreEqual(1, replay.Version); Assert.HasCount(0, replay.Definition.Bindings);
    }

    private static WorkProjectContextBinding ContractBinding(Fixture fixture)
    {
        var scope = ResourceScopeRef.Workspace(fixture.Workspace.Value);
        return new("tracking", WorkProjectBindingPurpose.Tracking,
            new(new(scope, default, "Tool", "tracking-describe"), Guid.NewGuid(), 1))
        {
            Contract = new(new(new(scope, default, "ToolProvider", "tracker"), Guid.NewGuid(), 1),
                new(new(scope, default, "ExtensionRegistration", "tracker"), Guid.NewGuid(), 1),
                "1.0", "tracker-authority", "no-repository", "binding", "revision-1", ["list"])
        };
    }

    private static WorkProjectContractEvidence? ContractEvidence(WorkProjectContextBinding binding)
    {
        if (binding.Contract is not { } selection) return null;
        WorkProjectContributionPin[] pins = [new(binding.Target, WorkPlanDefinition.Digest(binding.Target))];
        return new(selection.Version, "tracker-fixture", "1.0.0", selection.Provider, selection.Extension,
            WorkPlanDefinition.Digest(pins), new string('a', 64), ["list"], pins);
    }

    private static WorkProjectContextBinding Binding(Fixture fixture) => new("sharing", WorkProjectBindingPurpose.Sharing,
        new(new(ResourceScopeRef.Workspace(fixture.Workspace.Value), ResourceNamespace.Default, "ToolProvider", "git-operations"), Guid.NewGuid(), 1));
}
