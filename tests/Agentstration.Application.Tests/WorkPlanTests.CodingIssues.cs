using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task CodingTwoInstancesAcknowledgeOneSharedIssuePlanAndPreserveUnrelatedLabels()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var shared = new SharingProvider(); var native = new IssueProvider(); native.Put("1", labels: ["unrelated"]);
        var portableId = new WorkSharingProjectId(Guid.NewGuid());
        var lp = await IssueProjectAsync(first, shared, native, portableId); var rp = await IssueProjectAsync(second, shared, native, portableId);
        var plan = await IssuePlanAsync(first, lp.Id, "1");
        var command = await IssueCommandAsync(first, lp.Id, plan, "1");
        var acknowledged = await Issues(first, shared, native).AcknowledgeAsync(first.Workspace, lp.Id, command, default);
        Assert.IsTrue(acknowledged.IsSuccess, acknowledged.ErrorCode); Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, acknowledged.Value!.Status, acknowledged.Value.ErrorCode);
        var rightSharing = SharingService(second, shared);
        var projection = (await rightSharing.PullAsync(second.Workspace, rp.Id, null, default)).Value!;
        var record = projection.Records.Single();
        Assert.IsTrue((await rightSharing.ImportPlanAsync(second.Workspace, rp.Id, plan.Plan.Id, record.Digest, default)).IsSuccess);
        var local = Success(await second.Plans().GetAsync(second.Workspace, plan.Plan.Id, default));
        var secondCommand = await IssueCommandAsync(second, rp.Id, local, "1");
        var other = await Issues(second, shared, native).AcknowledgeAsync(second.Workspace, rp.Id, secondCommand, default);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, other.Value!.Status, other.ErrorCode ?? other.Value.ErrorCode);
        var issue = native.Tasks["1"];
        Assert.AreEqual(1, issue.Labels.Count(value => value.StartsWith("agentstration:plan:", StringComparison.Ordinal)));
        Assert.AreEqual(2, issue.Labels.Count(value => value.StartsWith("agentstration:instance:", StringComparison.Ordinal)));
        CollectionAssert.Contains(issue.Labels.ToArray(), "unrelated");
        var effects = native.Writes;
        await second.ReopenAsync();
        var replay = await Issues(second, shared, native).AcknowledgeAsync(second.Workspace, rp.Id, secondCommand, default);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, replay.Value!.Status); Assert.AreEqual(effects, native.Writes);
        Assert.AreEqual(plan.Plan.Id.Value, other.Value.PlanId);
    }

    [TestMethod]
    public async Task CodingNativeBlockedByExposesDifferentPlanAndRequiresCompletedRatherThanAbandonedClosure()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider();
        native.Put("1"); native.Put("2", relations: [new("blocked-by", "fixture/repository", "1")]);
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid()));
        var a = await IssuePlanAsync(fixture, project.Id, "1"); var b = await IssuePlanAsync(fixture, project.Id, "2");
        var service = Issues(fixture, shared, native);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, a, "1"), default)).Value!.Status);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, b, "2"), default)).Value!.Status);
        var blocked = (await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!;
        Assert.IsFalse(blocked.Eligible); Assert.AreEqual(a.Plan.Id.Value, blocked.Waits.Single().PlanId); Assert.AreEqual("1", blocked.Waits.Single().IssueId);
        native.Tasks["1"] = native.Tasks["1"] with { State = "closed", CompletionReason = "not_planned" };
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!.Eligible);
        native.Tasks["1"] = native.Tasks["1"] with { CompletionReason = null };
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!.Eligible);
        native.Tasks["1"] = native.Tasks["1"] with { CompletionReason = "completed" };
        var ready = (await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!;
        Assert.IsTrue(ready.Eligible); Assert.IsTrue(ready.Prerequisites.Single().Satisfied);
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [] };
        native.Tasks["1"] = native.Tasks["1"] with { State = "open", CompletionReason = null };
        Assert.IsTrue((await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!.Eligible);
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [new("blocked-by", "fixture/repository", "1")] };
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, b.Plan.Id, default)).Value!.Eligible);
    }

    [TestMethod]
    public async Task CodingNativeChildHasDistinctPlanWithoutInventedSequenceAndMissingUpstreamAuthorityBlocks()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider();
        native.Put("1", relations: [new("child", "fixture/repository", "2")]); native.Put("2", relations: [new("parent", "fixture/repository", "1")]);
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid()));
        var parent = await IssuePlanAsync(fixture, project.Id, "1"); var child = await IssuePlanAsync(fixture, project.Id, "2");
        Assert.AreNotEqual(parent.Plan.Id, child.Plan.Id);
        var service = Issues(fixture, shared, native);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, parent, "1"), default)).Value!.Status);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, child, "2"), default)).Value!.Status);
        Assert.IsTrue((await service.ReadinessAsync(fixture.Workspace, project.Id, parent.Plan.Id, default)).Value!.Eligible);
        Assert.IsTrue((await service.ReadinessAsync(fixture.Workspace, project.Id, child.Plan.Id, default)).Value!.Eligible);
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [new("blocked-by", "fixture/repository", "99")] };
        var missing = (await service.ReadinessAsync(fixture.Workspace, project.Id, child.Plan.Id, default)).Value!;
        Assert.IsFalse(missing.Eligible); Assert.AreEqual("upstream_plan_missing", missing.Waits.Single().Reason);
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [new("blocked-by", "fixture/repository", "1")] };
        native.Denied.Add("1");
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, child.Plan.Id, default)).Value!.Eligible);
    }

    [TestMethod]
    public async Task CodingConflictingPlanLabelsAndSharedAssociationsFailWithoutLastWriterWins()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider(); native.Put("1");
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid()));
        var a = await IssuePlanAsync(fixture, project.Id, "1"); var b = await IssuePlanAsync(fixture, project.Id, "other");
        var service = Issues(fixture, shared, native);
        var first = await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, a, "1"), default);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, first.Value!.Status);
        var conflict = await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, b, "1"), default);
        Assert.AreEqual(CodingIssueIntakeStatus.Conflict, conflict.Value!.Status); Assert.AreEqual(1, native.Writes);
        native.Tasks["1"] = native.Tasks["1"] with { Labels = [.. native.Tasks["1"].Labels, "agentstration:plan:" + b.Plan.Id] };
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, a.Plan.Id, default)).Value!.Eligible);
        native.Tasks["1"] = native.Tasks["1"] with { Labels = [] };
        conflict = await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, b, "1"), default);
        Assert.AreEqual(CodingIssueIntakeStatus.Conflict, conflict.Value!.Status); Assert.AreEqual(1, native.Writes);
    }

    [TestMethod]
    public async Task CodingLostAcknowledgmentIsPendingUntilRecoveredAndCurrentAuthorityIsRechecked()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider { LoseWriteResponse = true }; native.Put("1");
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid())); var plan = await IssuePlanAsync(fixture, project.Id, "1");
        var command = await IssueCommandAsync(fixture, project.Id, plan, "1"); var service = Issues(fixture, shared, native);
        var pending = await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default);
        Assert.AreEqual(CodingIssueIntakeStatus.Pending, pending.Value!.Status); Assert.AreEqual(1, native.Writes);
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, plan.Plan.Id, default)).Value!.Eligible);
        await fixture.ReopenAsync(); service = Issues(fixture, shared, native);
        var resumed = await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, resumed.Value!.Status); Assert.AreEqual(1, native.Writes);
        native.Denied.Add("1");
        Assert.IsFalse((await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default)).IsSuccess);
        Assert.AreEqual("work_project_not_found", (await service.ReadinessAsync(new(Guid.NewGuid()), project.Id, plan.Plan.Id, default)).ErrorCode);
    }

    [TestMethod]
    public async Task CodingDependencyChangesGateCanonicalDispatchWithoutDuplicateAttempts()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider();
        native.Put("1"); native.Put("2", relations: [new("blocked-by", "fixture/repository", "1")]);
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid()));
        var a = await IssuePlanAsync(fixture, project.Id, "1"); var b = await IssuePlanAsync(fixture, project.Id, "2");
        var service = Issues(fixture, shared, native);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, a, "1"), default)).Value!.Status);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, await IssueCommandAsync(fixture, project.Id, b, "2"), default)).Value!.Status);
        b = Success(await fixture.Plans().TransitionAsync(fixture.Workspace, b.Plan.Id, Phase(b, WorkPlanAction.Validate), default));
        b = Success(await fixture.Plans().TransitionAsync(fixture.Workspace, b.Plan.Id, Phase(b, WorkPlanAction.Activate), default));
        var tree = (await fixture.Trees().GetAsync(fixture.Workspace, b.Revision.Definition.TaskIds[0], default)).Value!;
        var definition = new WorkTaskTreeDefinition(tree.RootTaskId, tree.Tasks.Select(task => task.Definition).ToArray());
        var harness = new ExecutionHarness(fixture) { Gates = [new WorkProjectSharingAdmissionGate(SharingService(fixture, shared)), new CodingIssueAdmissionGate(service)] };
        await harness.InitializeAsync();
        b = Success(await harness.Service().StartAsync(fixture.Workspace, b.Plan.Id, Start(b, definition) with { ProjectId = project.Id }, default));
        var blocked = await harness.AdvanceAsync(b);
        Assert.AreEqual("coding_issue_dependencies_blocked", blocked.ErrorCode);
        Assert.HasCount(0, Success(await fixture.Plans().GetAsync(fixture.Workspace, b.Plan.Id, default)).Plan.Attempts);
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [] };
        b = Success(await harness.AdvanceAsync(b));
        Assert.HasCount(1, b.Plan.Attempts); Assert.HasCount(2, b.Plan.Attempts.Single().AdmissionEvidence);
        // Reintroducing an upstream wait does not duplicate or cancel already admitted local work.
        native.Tasks["2"] = native.Tasks["2"] with { Relations = [new("blocked-by", "fixture/repository", "1")] };
        b = Success(await harness.AdvanceAsync(b));
        Assert.HasCount(1, b.Plan.Attempts); Assert.IsFalse(b.Plan.Attempts.Single().CancellationRequested);
    }

    [TestMethod]
    public async Task CodingSharedIssueClaimsRejectCaseVariantsAndCannotBeDetachedByOrdinaryPublication()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider(); native.Put("1");
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid()));
        var a = await IssuePlanAsync(fixture, project.Id, "1"); var b = await IssuePlanAsync(fixture, project.Id, "other");
        var command = await IssueCommandAsync(fixture, project.Id, a, "1");
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await Issues(fixture, shared, native).AcknowledgeAsync(fixture.Workspace, project.Id, command, default)).Value!.Status);
        var connector = SharingService(fixture, shared); var current = ProjectSuccess(await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default));
        var issue = new WorkSharingIssueReference("fixture", "FIXTURE/REPOSITORY", "1", command.PlanLabelPrefix, command.ParticipantLabelPrefix);
        var competing = (await connector.ExportPlanAsync(fixture.Workspace, b.Plan.Id,
            b.Revision.Definition.TaskIds.Select(id => new WorkTaskAllocation(id, current.Definition.Sharing!.ParticipantId)).ToArray(), default, issue)).Value!;
        var conflict = await connector.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), current.Version,
            [new(new(WorkSharingDomain.Plan, b.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(competing.Plan, SharingJson))]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Conflict, conflict.Value!.Status); Assert.AreEqual("sharing_issue_plan_conflict", conflict.Value.ErrorCode);
        var detached = (await connector.ExportPlanAsync(fixture.Workspace, a.Plan.Id, command.Allocation, default)).Value!;
        conflict = await connector.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), current.Version,
            [new(new(WorkSharingDomain.Plan, a.Plan.Id.ToString()), 1, JsonSerializer.SerializeToElement(detached.Plan, SharingJson))]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Conflict, conflict.Value!.Status); Assert.AreEqual("sharing_issue_association_immutable", conflict.Value.ErrorCode);
    }

    [TestMethod]
    public async Task CodingAcknowledgmentMappingUpgradesExistingSqliteAndRequiresAConfirmedOwnedReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(); var shared = new SharingProvider(); var native = new IssueProvider { LoseWriteResponse = true }; native.Put("1");
        var project = await IssueProjectAsync(fixture, shared, native, new(Guid.NewGuid())); var plan = await IssuePlanAsync(fixture, project.Id, "1");
        var command = await IssueCommandAsync(fixture, project.Id, plan, "1"); var service = Issues(fixture, shared, native);
        var pending = (await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default)).Value!;
        Assert.AreEqual(CodingIssueIntakeStatus.Pending, pending.Status);
        Assert.IsFalse(await fixture.Sharing.SetPlanOperationAsync(fixture.Workspace, fixture.Principal, project.Id, plan.Plan.Id, new(pending.IssueOperationId!.Value), default));
        var confirmed = (await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default)).Value!;
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, confirmed.Status);
        Assert.IsNotNull(await fixture.Sharing.GetPlanOperationAsync(fixture.Workspace, fixture.Principal, project.Id, plan.Plan.Id, default));
        Assert.IsNull(await fixture.Sharing.GetPlanOperationAsync(fixture.Workspace, Guid.NewGuid(), project.Id, plan.Plan.Id, default));
        await fixture.SqlAsync("ALTER TABLE WorkSharingPlanMappings DROP COLUMN OperationId;");
        await fixture.ReopenAsync(); await SharingService(fixture, shared).InitializeAsync(default);
        service = Issues(fixture, shared, native);
        Assert.IsFalse((await service.ReadinessAsync(fixture.Workspace, project.Id, plan.Plan.Id, default)).Value!.Eligible);
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await service.AcknowledgeAsync(fixture.Workspace, project.Id, command, default)).Value!.Status);
        Assert.AreEqual(1, native.Writes);
    }

    private static CodingIssueCoordinationService Issues(Fixture fixture, SharingProvider shared, IssueProvider native) => new(ProjectService(fixture), fixture.Plans(),
        SharingService(fixture, shared), TaskProviders(fixture, native), new IssuePolicy(), new Context(fixture.Workspace, fixture.Principal), new Clock(Now));
    private static async Task<WorkProjectSnapshot> IssueProjectAsync(Fixture fixture, SharingProvider shared, IssueProvider native, WorkSharingProjectId portable)
    {
        var (_, project) = await SharingAsync(fixture, shared, portable, new(Guid.NewGuid()));
        var binding = new WorkProjectContextBinding("issues", WorkProjectBindingPurpose.TaskOperations,
            new(ScopedResourceAddress.Create(ResourceScopeRef.Workspace(fixture.Workspace.Value), default, "Tool", "native-issues"), Guid.NewGuid(), 1));
        project = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { Bindings = [.. project.Definition.Bindings, binding] }), default));
        await TaskProviders(fixture, native).InitializeAsync(default); return project;
    }
    private static async Task<WorkPlanView> IssuePlanAsync(Fixture fixture, WorkProjectId projectId, string issue)
    {
        var taskId = new WorkTaskId(Guid.NewGuid());
        await fixture.CreateTreeAsync(new(taskId, [new(taskId, WorkTaskKind.Atomic, "Issue " + issue, "Deliver issue " + issue)]));
        var plan = Success(await fixture.Plans().CreateAsync(fixture.Workspace, WorkPlanId.New(), new(Guid.NewGuid(), 0, new("Deliver " + issue, [taskId], [])), default));
        var project = ProjectSuccess(await ProjectService(fixture).GetAsync(fixture.Workspace, projectId, default));
        ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, projectId, new(Guid.NewGuid(), project.Version,
            project.Definition with { PlanIds = [.. project.Definition.PlanIds, plan.Plan.Id] }), default)); return plan;
    }
    private static async Task<AcknowledgeCodingIssueRequest> IssueCommandAsync(Fixture fixture, WorkProjectId projectId, WorkPlanView plan, string issue)
    {
        var project = ProjectSuccess(await ProjectService(fixture).GetAsync(fixture.Workspace, projectId, default));
        return new(Guid.NewGuid(), project.Version, plan.Plan.Id.Value, "fixture/repository", issue, "fixture", "agentstration:plan:", "agentstration:instance:", 0,
            plan.Revision.Definition.TaskIds.Select(task => new WorkTaskAllocation(task, project.Definition.Sharing!.ParticipantId)).ToArray());
    }
    private sealed class IssuePolicy : ICodingIssueBindingPolicy
    {
        public Task<Result<CodingIssueBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkSharingIssueReference issue, CancellationToken token) => Task.FromResult(Result<CodingIssueBinding>.Success(new(issue.Provider, "agentstration:")));
    }
    private sealed class IssueProvider : IWorkTaskProviderGateway
    {
        public Dictionary<string, ExternalTaskSnapshot> Tasks { get; } = [];
        public HashSet<string> Denied { get; } = [];
        public int Writes { get; private set; }
        public bool LoseWriteResponse { get; set; }
        private readonly Dictionary<string, ExternalTaskProviderResponse> receipts = [];
        public void Put(string id, IReadOnlyList<string>? labels = null, IReadOnlyList<ExternalTaskRelation>? relations = null) => Tasks[id] =
            new("fixture/repository", id, "initial", "Issue " + id, null, "open", new("https://fixture.test/issues/" + id), labels ?? [], relations ?? []);
        public Task<Result<WorkTaskProviderRoute>> ResolveAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            ExternalTaskOperationKind operation, CancellationToken token) => Task.FromResult(Result<WorkTaskProviderRoute>.Success(new(binding, binding.Target,
                new("1.0", [ExternalTaskOperationKind.Read, ExternalTaskOperationKind.Update], true, true))));
        public Task<ExternalTaskProviderResponse> InvokeAsync(WorkTaskOperation operation, bool recover, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var id = operation.Input.TaskId!;
            ExternalTaskProviderResponse Response(ExternalTaskProviderStatus status, ExternalTaskSnapshot? task = null, string? error = null) => new(operation.OperationKey, operation.IntentDigest, status, task, error);
            if (Denied.Contains(id) || !Tasks.TryGetValue(id, out var task)) return Task.FromResult(Response(ExternalTaskProviderStatus.Rejected, error: "native_access_denied"));
            task = task with { Revision = WorkPlanDefinition.Digest(task with { Revision = "" }) }; Tasks[id] = task;
            if (operation.Input.Operation == ExternalTaskOperationKind.Read) return Task.FromResult(Response(ExternalTaskProviderStatus.Succeeded, task));
            if (receipts.TryGetValue(operation.OperationKey, out var receipt)) return Task.FromResult(receipt);
            if (recover) return Task.FromResult(Response(ExternalTaskProviderStatus.Missing));
            if (operation.Input.ExpectedRevision != task.Revision) return Task.FromResult(Response(ExternalTaskProviderStatus.Rejected, error: "native_revision_changed"));
            task = task with { Labels = [.. task.Labels.Union(operation.Input.Labels!, StringComparer.Ordinal)] }; Tasks[id] = task; Writes++;
            receipt = Response(ExternalTaskProviderStatus.Succeeded, task); receipts[operation.OperationKey] = receipt;
            if (LoseWriteResponse) { LoseWriteResponse = false; return Task.FromResult(Response(ExternalTaskProviderStatus.Uncertain)); }
            return Task.FromResult(receipt);
        }
    }
}
