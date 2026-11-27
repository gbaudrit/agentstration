using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Tests.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task SharingConnectionsStayPortableAcrossIndependentStoresAndImportPreservesLocalAssignments()
    {
        var source = new ProjectContractFixture(); var destination = new ProjectContractFixture();
        using var sourceScope = source.Context.Push(source.Caller); using var destinationScope = destination.Context.Push(destination.Caller);
        await using var first = await Fixture.CreateAsync(source.WorkspaceId, source.PrincipalId);
        await using var second = await Fixture.CreateAsync(destination.WorkspaceId, destination.PrincipalId);
        var remote = new SharingProvider(); var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var (left, lp) = await SharingAsync(first, remote, portable, participant);
        var (right, rp) = await SharingAsync(second, remote, portable, new(Guid.NewGuid()));
        var sourceRoute = source.AddConnection(true, "tracking");
        var destinationRoute = destination.AddConnection(true, "tracking", ResourceScopeRef.Instance);
        var backend = source.AddService(sourceRoute, "backend"); var frontend = source.AddService(sourceRoute, "frontend", targetId: "frontend");
        var ownBackend = destination.AddService(destinationRoute, "backend", instanceService: true);
        var ownFrontend = destination.AddService(destinationRoute, "frontend", targetId: "frontend");
        var needs = new[] { backend.Requirement, frontend.Requirement };
        var leftAssignment = backend.Assignment; var rightAssignment = ownBackend.Assignment;
        var sourceBindings = new SharingConnectionBindings(source.Resolver); var destinationBindings = new SharingConnectionBindings(destination.Resolver);
        left = SharingService(first, remote, bindings: sourceBindings); right = SharingService(second, remote, bindings: destinationBindings);
        var task = new WorkTaskId(Guid.NewGuid());
        var tree = await first.CreateTreeAsync(new(task, [new(task, WorkTaskKind.Atomic, "Implement", "Keep execution local")]));
        var plan = Success(await first.Plans().CreateAsync(first.Workspace, WorkPlanId.New(), new(Guid.NewGuid(), 0,
            new("Portable dependencies", [task], []) { TaskConnections = [new(task, ["backend", "frontend"])] }), default));
        lp = ProjectSuccess(await ProjectService(first, bindings: sourceBindings).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { PlanIds = [plan.Plan.Id], ConnectionRequirements = needs, ConnectionAssignments = [leftAssignment] }), default));
        rp = ProjectSuccess(await ProjectService(second, bindings: destinationBindings).UpdateAsync(second.Workspace, rp.Id, new(Guid.NewGuid(), rp.Version,
            rp.Definition with { ConnectionRequirements = [needs[0]], ConnectionAssignments = [rightAssignment] }), default));
        var change = SharingMetadata(needs, participant);
        var exported = (await left.ExportPlanAsync(first.Workspace, plan.Plan.Id, [new(task, participant)], default)).Value!;
        var planChange = new WorkSharingChange(new(WorkSharingDomain.Plan, plan.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(exported.Plan, SharingJson));
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await left.PublishAsync(first.Workspace, lp.Id,
            new(new(Guid.NewGuid()), lp.Version, [change, planChange]), default)).Value!.Status);
        var pulled = (await right.PullAsync(second.Workspace, rp.Id, null, default)).Value!;
        Assert.HasCount(1, ProjectSuccess(await ProjectService(second).GetAsync(second.Workspace, rp.Id, default)).Definition.ConnectionRequirements);
        var record = pulled.Records.Single(value => value.Key == WorkSharingProjectMetadata.Key);
        var applied = ProjectSuccess(await right.ImportConnectionsAsync(second.Workspace, rp.Id, rp.Version, record.Digest, default));
        Assert.HasCount(2, applied.Definition.ConnectionRequirements);
        Assert.AreEqual(JsonSerializer.Serialize(rp.Definition.ConnectionAssignments), JsonSerializer.Serialize(applied.Definition.ConnectionAssignments));
        Assert.AreEqual(JsonSerializer.Serialize(rp.Definition.Bindings), JsonSerializer.Serialize(applied.Definition.Bindings));
        Assert.AreEqual(rp.Definition.Sharing, applied.Definition.Sharing);
        Assert.AreNotEqual(leftAssignment.Service.Uid, applied.Definition.ConnectionAssignments.Single().Service.Uid);
        Assert.AreNotEqual(leftAssignment.Service.Address.ScopeRef, rightAssignment.Service.Address.ScopeRef);
        Assert.AreNotEqual(sourceRoute.Contract!.Extension.Uid, destinationRoute.Contract!.Extension.Uid);
        var draft = Success(await right.ImportPlanAsync(second.Workspace, rp.Id, plan.Plan.Id,
            pulled.Records.Single(value => value.Key.Domain == WorkSharingDomain.Plan).Digest, default));
        Assert.AreEqual(plan.Revision.Digest, draft.Revision.Digest); Assert.IsNull(draft.Plan.Execution); Assert.HasCount(0, draft.Plan.Attempts);
        applied = ProjectSuccess(await ProjectService(second).GetAsync(second.Workspace, rp.Id, default));
        // Resolve both same-contract needs using this installation's explicit local assignments.
        applied = ProjectSuccess(await ProjectService(second, bindings: destinationBindings).UpdateAsync(second.Workspace, rp.Id,
            new(Guid.NewGuid(), applied.Version, applied.Definition with { ConnectionAssignments = [rightAssignment, ownFrontend.Assignment] }), default));
        var context = await ProjectContexts(second, destinationBindings).CaptureAsync(second.Workspace, rp.Id, plan.Plan.Id, default, ["backend", "frontend"]);
        Assert.IsTrue(context.IsSuccess, context.ErrorCode);
        CollectionAssert.AreEquivalent(new[] { "backend", "frontend" }, context.Value!.Bindings.Where(value => value.Binding.Connection is not null).Select(value => value.Binding.Name).ToArray());
        var mismatch = await ProjectService(second, bindings: destinationBindings).UpdateAsync(second.Workspace, rp.Id,
            new(Guid.NewGuid(), applied.Version, applied.Definition with
            { ConnectionRequirements = [needs[0] with { Target = new(needs[0].Target!.ProviderId, "foreign") }, needs[1]] }), default);
        Assert.AreEqual("work_project_connection_target_mismatch", mismatch.ErrorCode);
        var wire = JsonSerializer.Serialize(pulled.Records);
        foreach (var excluded in new[] { first.Workspace.ToString(), first.Principal.ToString(), leftAssignment.Service.Uid.ToString(), leftAssignment.DescribeTool.Uid.ToString(), "connectionAssignments", "scopeRef" })
            Assert.DoesNotContain(excluded, wire);
        Assert.AreEqual("work_project_conflict", (await right.ImportConnectionsAsync(second.Workspace, rp.Id, rp.Version, new string('a', 64), default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await right.ImportConnectionsAsync(first.Workspace, rp.Id, applied.Version, record.Digest, default)).ErrorCode);
        Assert.AreEqual("work_project_not_found", (await SharingService(second, remote, Guid.NewGuid()).ImportConnectionsAsync(second.Workspace, rp.Id, applied.Version, record.Digest, default)).ErrorCode);
        // A completed import cannot reapply old configuration over a later local edit, including after restart.
        var edited = ProjectSuccess(await ProjectService(second, bindings: destinationBindings).UpdateAsync(second.Workspace, rp.Id,
            new(Guid.NewGuid(), applied.Version, applied.Definition with { ConnectionRequirements = [needs[0]], ConnectionAssignments = [rightAssignment] }), default));
        await second.ReopenAsync(); right = SharingService(second, remote, bindings: destinationBindings);
        var replay = ProjectSuccess(await right.ImportConnectionsAsync(second.Workspace, rp.Id, rp.Version, record.Digest, default));
        Assert.AreEqual(edited.Version, replay.Version); Assert.HasCount(1, replay.Definition.ConnectionRequirements);
        Assert.AreEqual(1, remote.Publications);
    }

    [TestMethod]
    public async Task SharingConnectionImportRefusesToRemoveLocalAssignmentsOrApplyUnpinnedMetadata()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var remote = new SharingProvider(); var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var (left, lp) = await SharingAsync(first, remote, portable, participant);
        var (right, rp) = await SharingAsync(second, remote, portable, new(Guid.NewGuid()));
        var need = SharingNeed("backend");
        rp = ProjectSuccess(await ProjectService(second).UpdateAsync(second.Workspace, rp.Id, new(Guid.NewGuid(), rp.Version,
            rp.Definition with { ConnectionRequirements = [need], ConnectionAssignments = [SharingAssignment(second, need.Name)] }), default));
        Assert.IsTrue((await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version, [SharingMetadata([], participant)]), default)).IsSuccess);
        var record = (await right.PullAsync(second.Workspace, rp.Id, null, default)).Value!.Records.Single();
        Assert.AreEqual("sharing_record_conflict", (await right.ImportConnectionsAsync(second.Workspace, rp.Id, rp.Version, new string('f', 64), default)).ErrorCode);
        Assert.AreEqual("work_project_definition_invalid", (await right.ImportConnectionsAsync(second.Workspace, rp.Id, rp.Version, record.Digest, default)).ErrorCode);
        var current = ProjectSuccess(await ProjectService(second).GetAsync(second.Workspace, rp.Id, default));
        Assert.AreEqual(rp.Version, current.Version); Assert.HasCount(1, current.Definition.ConnectionAssignments);
    }

    [TestMethod]
    public async Task SharedPlanConnectionsSurviveImportDriveLocalSelectionAndRejectChangedRequirementsAtAdmission()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var remote = new SharingProvider(); var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var (left, lp) = await SharingAsync(first, remote, portable, participant);
        var (right, rp) = await SharingAsync(second, remote, portable, new(Guid.NewGuid()));
        var tree = await first.CreateTreeAsync(); var command = Command(tree);
        var atomic = tree.Tasks.First(value => value.Kind == WorkTaskKind.Atomic).Id;
        command = command with { Definition = command.Definition with { TaskConnections = [new(atomic, ["frontend", "backend"])] } };
        var plan = Success(await first.Plans().CreateAsync(first.Workspace, WorkPlanId.New(), command, default));
        plan = Success(await first.Plans().TransitionAsync(first.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Validate), default));
        plan = Success(await first.Plans().TransitionAsync(first.Workspace, plan.Plan.Id, Phase(plan, WorkPlanAction.Activate), default));
        var needs = new[] { SharingNeed("backend"), SharingNeed("frontend") };
        lp = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with
            {
                PlanIds = [plan.Plan.Id],
                ConnectionRequirements = needs,
                ConnectionAssignments = needs.Select(value => SharingAssignment(first, value.Name)).ToArray()
            }), default));
        var exported = (await left.ExportPlanAsync(first.Workspace, plan.Plan.Id,
            tree.Tasks.Where(value => value.Kind == WorkTaskKind.Atomic).Select(value => new WorkTaskAllocation(value.Id, participant)).ToArray(), default)).Value!;
        var planChange = new WorkSharingChange(new(WorkSharingDomain.Plan, plan.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(exported.Plan, SharingJson));
        var missing = await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version, [planChange]), default);
        Assert.AreEqual("sharing_connection_requirement_missing", missing.Value!.ErrorCode); Assert.AreEqual(0, remote.Publications);
        var unbound = needs.Select(need => need with { Target = null }).ToArray();
        lp = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionRequirements = unbound }), default));
        var noTarget = await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version,
            [planChange, SharingMetadata(unbound, participant)]), default);
        Assert.AreEqual("sharing_connection_target_required", noTarget.Value!.ErrorCode); Assert.AreEqual(0, remote.Publications);
        lp = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionRequirements = needs }), default));
        var unapproved = SharingMetadata([needs[0] with { Target = new("foreign", "repository") }, needs[1]], participant);
        Assert.AreEqual("sharing_local_connections_changed", (await left.PublishAsync(first.Workspace, lp.Id,
            new(new(Guid.NewGuid()), lp.Version, [unapproved]), default)).ErrorCode);
        var published = await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version, [planChange, SharingMetadata(needs, participant)]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, published.Value!.Status);
        var snapshot = (await right.PullAsync(second.Workspace, rp.Id, null, default)).Value!;
        var imported = Success(await right.ImportPlanAsync(second.Workspace, rp.Id, plan.Plan.Id,
            snapshot.Records.Single(value => value.Key.Domain == WorkSharingDomain.Plan).Digest, default));
        Assert.AreEqual(plan.Revision.Digest, imported.Revision.Digest);
        CollectionAssert.AreEqual(new[] { "backend", "frontend" }, imported.Revision.Definition.TaskConnections.Single().Names.ToArray());
        Assert.HasCount(0, imported.Plan.Attempts); Assert.IsNull(imported.Plan.Execution);
        var harness = new ExecutionHarness(first) { Gates = [new WorkProjectSharingAdmissionGate(left)] }; await harness.InitializeAsync();
        var start = Start(plan, tree) with { ProjectId = lp.Id };
        var invalid = start with { Targets = start.Targets.Select(value => value.TaskId == atomic ? value with { Connections = ["backend"] } : value).ToArray() };
        Assert.AreEqual("work_plan_connections_mismatch", (await harness.Service().StartAsync(first.Workspace, plan.Plan.Id, invalid, default)).ErrorCode);
        plan = Success(await harness.Service().StartAsync(first.Workspace, plan.Plan.Id, start, default));
        CollectionAssert.AreEqual(new[] { "backend", "frontend" }, plan.Plan.Execution!.Targets.Single(value => value.TaskId == atomic).Connections.ToArray());
        var gate = new WorkProjectSharingAdmissionGate(left); var scope = plan.Plan.Execution.Scope;
        var evidence = await gate.ObserveAsync(scope, plan.Plan, plan.Revision, atomic, default);
        Assert.IsTrue(evidence.IsSuccess, evidence.ErrorCode); Assert.HasCount(2, evidence.Value!);
        plan = Success(await harness.AdvanceAsync(plan));
        var retained = JsonSerializer.Serialize(plan.Plan.Attempts.Single().ProjectContext);
        Assert.HasCount(2, plan.Plan.Attempts.Single().AdmissionEvidence);
        Assert.HasCount(2, plan.Plan.Attempts.Single().ProjectContext!.Bindings.Where(value => value.Binding.Connection is not null));
        var forged = plan.Plan with { Execution = plan.Plan.Execution! with { Targets = [] } };
        Assert.AreEqual("sharing_task_connections_mismatch", (await gate.ObserveAsync(scope, forged, plan.Revision, atomic, default)).ErrorCode);
        lp = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionRequirements = [needs[0] with { Target = new("foreign", "repository") }, needs[1]] }), default));
        Assert.AreEqual("sharing_connection_requirement_mismatch", (await gate.ObserveAsync(scope, plan.Plan, plan.Revision, atomic, default)).ErrorCode);
        // Removing a declaration still consumed by a published plan cannot be merged.
        lp = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionRequirements = [], ConnectionAssignments = [] }), default));
        var removal = await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version,
            [SharingMetadata([], participant) with { ExpectedVersion = 1 }]), default);
        Assert.AreEqual("sharing_connection_requirement_missing", removal.Value!.ErrorCode);
        Assert.AreEqual(1, remote.Publications);
        await first.ReopenAsync();
        var restored = Success(await first.Plans().GetAsync(first.Workspace, plan.Plan.Id, default));
        Assert.AreEqual(retained, JsonSerializer.Serialize(restored.Plan.Attempts.Single().ProjectContext));
    }

    [TestMethod]
    public async Task PortableConnectionDeclarationsRejectPhysicalConfigurationAndNormalizeFunctionalIdentity()
    {
        var participant = new WorkParticipantId(Guid.NewGuid()); var need = SharingNeed("backend");
        var change = SharingMetadata([need], participant);
        foreach (var key in new[] { "connectionAssignments", "connectedService", "scopeRef", "uid", "secretValue" })
        {
            var raw = JsonNode.Parse(change.Content.GetRawText())!;
            raw["connectionRequirements"]![0]![key] = "local-only";
            Assert.AreEqual("sharing_content_invalid", WorkSharingContent.Normalize(change with { Content = JsonSerializer.SerializeToElement(raw) }).ErrorCode);
        }
        Assert.IsTrue(WorkSharingContent.ValidIssue(new("fixture", "board/project", "ENG-123", "plan:", "participant:")
        { ConnectionName = "backend", Tracking = need.Target }), "Portable item identities must not inherit GitHub numeric-ID rules.");
        Assert.IsFalse(WorkSharingContent.Normalize(SharingMetadata([need, need], participant)).IsSuccess);
        Assert.IsFalse(WorkSharingContent.Normalize(change with { Key = new(WorkSharingDomain.ProjectMetadata, "other") }).IsSuccess);
        var task = new WorkTaskId(Guid.NewGuid());
        var definition = new WorkPlanDefinition("Portable", [task], []) { TaskConnections = [new(task, ["frontend", "backend"])] };
        var normalized = definition.Validate().Value!;
        var other = (definition with { TaskConnections = [new(task, ["backend", "frontend"])] }).Validate().Value!;
        Assert.AreEqual(normalized.ContentDigest([]), other.ContentDigest([]));
        Assert.AreNotEqual(normalized.ContentDigest([]), (normalized with { TaskConnections = [] }).ContentDigest([]));
        Assert.IsFalse((definition with { TaskConnections = [new(task, ["backend", "backend"])] }).Validate().IsSuccess);
        Assert.IsFalse((definition with { TaskConnections = [new(new(Guid.NewGuid()), ["backend"])] }).Validate().IsSuccess);
        var request = new WorkPlanDefinitionRequest("Portable", [task.Value], [])
        { TaskConnections = [new(task.Value, ["backend", "frontend"])] };
        Assert.AreEqual(normalized.ContentDigest([]), WorkPlanContractMapping.ToPlanDefinition(request).Validate().Value!.ContentDigest([]));
    }

    private sealed class SharingConnectionBindings(IWorkProjectBindingResolver connections) : IWorkProjectBindingResolver
    {
        private readonly ProjectBindings ordinary = new();
        public Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
            WorkProjectContextBinding binding, CancellationToken token) => binding.Connection is { } selection
                ? connections.ResolveConnectionAsync(workspaceId, principalId, selection, token)
                : ordinary.ResolveAsync(workspaceId, principalId, binding, token);
        public Task<Result<WorkProjectResolvedBinding>> ResolveConnectionAsync(WorkspaceId workspaceId, Guid principalId,
            WorkProjectConnectionSelection selection, CancellationToken token) => connections.ResolveConnectionAsync(workspaceId, principalId, selection, token);
    }

    private static WorkProjectConnectionRequirement SharingNeed(string name) => new(name, WorkProjectConnectionRules.Tracking, "1.0", ["list"], new("tracker-authority", "no-repository"));
    private static WorkProjectConnectionAssignment SharingAssignment(Fixture fixture, string name, ResourceScopeRef? scope = null)
    {
        var tool = ContractBinding(fixture).Target;
        if (scope is { } owner) tool = tool with { Address = tool.Address with { ScopeRef = owner } };
        return new(name, new(tool.Address with { Kind = "ConnectedService", Name = name }, Guid.NewGuid(), 1), tool,
            WorkProjectConnectionRules.TargetFields.Select(field => new WorkProjectConnectionExport(field, field)).ToArray());
    }
    private static WorkSharingChange SharingMetadata(IReadOnlyList<WorkProjectConnectionRequirement> needs, WorkParticipantId participant) =>
        new(WorkSharingProjectMetadata.Key, 0, JsonSerializer.SerializeToElement(new WorkSharingProjectMetadata("Shared", null, [participant])
        { ConnectionRequirements = needs }, SharingJson));
}
