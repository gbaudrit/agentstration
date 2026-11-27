using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task NamedConnectionsAreSelectedPerTaskAndActiveAttemptsKeepTheirLocalAssignmentAcrossRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tree = await fixture.CreateTreeAsync(); var plan = await ActiveAsync(fixture, tree);
        var adapter = ContractBinding(fixture);
        var requirement = new WorkProjectConnectionRequirement("tracking", WorkProjectConnectionRules.Tracking, "1.0", ["list"]);
        var assignment = new WorkProjectConnectionAssignment("tracking", new(adapter.Target.Address with { Kind = "ConnectedService" }, Guid.NewGuid(), 1),
            adapter.Target, WorkProjectConnectionRules.TargetFields.Select(field => new WorkProjectConnectionExport(field, field)).ToArray());
        var bindings = new ProjectBindings(); var projects = ProjectService(fixture, bindings: bindings); await projects.InitializeAsync(default);
        var create = new Agentstration.Application.Work.SaveWorkProjectCommand(Guid.NewGuid(), 0,
            new("Named connections", null, [plan.Plan.Id], [])
            { ConnectionRequirements = [requirement, requirement with { Name = "unused" }], ConnectionAssignments = [assignment] });
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "named", default, create, default));
        Assert.AreEqual("work_project_command_conflict", (await projects.CreateAsync(fixture.Workspace, project.Id, "named", default,
            create with { Definition = create.Definition with { ConnectionRequirements = [requirement] } }, default)).ErrorCode,
            "Connection declarations must participate in idempotent command digests.");
        var contexts = ProjectContexts(fixture, bindings);
        var availability = (await contexts.GetAvailabilityAsync(fixture.Workspace, project.Id, default)).Value!;
        Assert.AreEqual("work_project_connection_unconfigured", availability.Connections.Single(value => value.Requirement.Name == "unused").Code);
        bindings.ConnectionCalls.Clear();
        Assert.HasCount(0, (await contexts.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, default)).Value!.Bindings);
        Assert.HasCount(0, bindings.ConnectionCalls, "No-provider work must not resolve unrelated requirements.");
        Assert.AreEqual("work_project_connection_unconfigured", (await contexts.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, default, ["unused"])).ErrorCode);
        Assert.AreEqual("work_project_connection_unknown", (await contexts.CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, default, ["foreign"])).ErrorCode);
        var harness = new ExecutionHarness(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        var start = Start(plan, tree) with { ProjectId = project.Id };
        start = start with { Targets = start.Targets.Select(target => target with { Connections = ["tracking"] }).ToArray() };
        plan = Success(await harness.Service().StartAsync(fixture.Workspace, plan.Plan.Id, start, default));
        plan = Success(await harness.AdvanceAsync(plan));
        var first = plan.Plan.Attempts.Single(); var retained = JsonSerializer.Serialize(first.ProjectContext);
        Assert.AreEqual(assignment.Service, first.ProjectContext!.Bindings.Single().Service!.Service);
        var replacement = assignment with { Service = assignment.Service with { Uid = Guid.NewGuid() } };
        project = ProjectSuccess(await projects.UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), project.Version, project.Definition with { ConnectionAssignments = [replacement] }), default));
        await fixture.ReopenAsync();
        harness = new(fixture) { Bindings = bindings }; await harness.InitializeAsync();
        plan = Success(await harness.AdvanceAsync(plan));
        Assert.AreEqual(retained, JsonSerializer.Serialize(plan.Plan.Attempts.Single().ProjectContext));
        Assert.AreEqual(replacement.Service, (await ProjectContexts(fixture, bindings).CaptureAsync(fixture.Workspace, project.Id, plan.Plan.Id, default, ["tracking"])).Value!.Bindings.Single().Service!.Service);
        bindings.Failure = "work_project_binding_denied";
        Assert.AreEqual("work_project_binding_denied", (await ProjectContexts(fixture, bindings).RevalidateAsync(fixture.Workspace, first.ProjectContext.Bindings.Single(), default)).ErrorCode);
        Assert.AreEqual(retained, JsonSerializer.Serialize(plan.Plan.Attempts.Single().ProjectContext));
    }
}
