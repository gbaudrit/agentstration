using System.Text.Json;
using Agentstration.Infrastructure.Work;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task SharingProjectsExchangePortablePlansAndExplicitlyImportDraftsAcrossIndependentStores()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var remote = new SharingProvider(); var portableProject = new WorkSharingProjectId(Guid.NewGuid());
        var participant = new WorkParticipantId(Guid.NewGuid());
        var (left, leftProject) = await SharingAsync(first, remote, portableProject, participant);
        var (right, rightProject) = await SharingAsync(second, remote, portableProject, new(Guid.NewGuid()));
        var tree = await first.CreateTreeAsync(); var local = await ActiveAsync(first, tree);
        leftProject = ProjectSuccess(await ProjectService(first).UpdateAsync(first.Workspace, leftProject.Id, new(Guid.NewGuid(), leftProject.Version,
            leftProject.Definition with { PlanIds = [local.Plan.Id] }), default));
        var exported = (await left.ExportPlanAsync(first.Workspace, local.Plan.Id,
            tree.Tasks.Where(task => task.Kind == WorkTaskKind.Atomic).Select(task => new WorkTaskAllocation(task.Id, participant)).ToArray(), default)).Value!;
        var change = new WorkSharingChange(new(WorkSharingDomain.Plan, local.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(exported.Plan, SharingJson));
        var operation = await left.PublishAsync(first.Workspace, leftProject.Id, new(new(Guid.NewGuid()), leftProject.Version, [change]), default);
        Assert.IsTrue(operation.IsSuccess, operation.ErrorCode); Assert.AreEqual(WorkSharingOperationStatus.Succeeded, operation.Value!.Status);
        var pulled = await right.PullAsync(second.Workspace, rightProject.Id, null, default);
        Assert.IsTrue(pulled.IsSuccess, pulled.ErrorCode);
        var record = pulled.Value!.Records.Single();
        Assert.AreEqual(local.Plan.Id, record.Content.Deserialize<WorkSharingPlan>(SharingJson)!.PlanId);
        var imported = await right.ImportPlanAsync(second.Workspace, rightProject.Id, local.Plan.Id, record.Digest, default);
        Assert.IsTrue(imported.IsSuccess, imported.ErrorCode); Assert.AreEqual(WorkPlanStatus.Draft, imported.Value!.Plan.Status);
        Assert.AreEqual(second.Principal, imported.Value.Plan.OwnerPrincipalId); Assert.AreEqual(second.Workspace, imported.Value.Plan.WorkspaceId);
        Assert.IsNull(imported.Value.Plan.Execution); Assert.HasCount(0, imported.Value.Plan.Attempts);
        var replay = await right.ImportPlanAsync(second.Workspace, rightProject.Id, local.Plan.Id, record.Digest, default);
        Assert.IsTrue(replay.IsSuccess, replay.ErrorCode); Assert.AreEqual(imported.Value.Plan.Version, replay.Value!.Plan.Version);
        var wire = JsonSerializer.Serialize(pulled.Value.Space) + JsonSerializer.Serialize(pulled.Value.Records);
        Assert.IsFalse(wire.Contains(first.Workspace.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(first.Principal.ToString(), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SharingDisjointConcurrentContributionsMergeAndReplayOnceAfterRestart()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var remote = new SharingProvider { Concurrent = new() }; var projectId = new WorkSharingProjectId(Guid.NewGuid());
        var (left, lp) = await SharingAsync(first, remote, projectId, new(Guid.NewGuid()));
        var (right, rp) = await SharingAsync(second, remote, projectId, new(Guid.NewGuid()));
        var one = new PublishWorkSharingCommand(new(Guid.NewGuid()), lp.Version, [ContributionChange()]);
        var two = new PublishWorkSharingCommand(new(Guid.NewGuid()), rp.Version, [ContributionChange()]);
        var results = await Task.WhenAll(left.PublishAsync(first.Workspace, lp.Id, one, default), right.PublishAsync(second.Workspace, rp.Id, two, default));
        Assert.IsTrue(results.All(value => value.IsSuccess && value.Value!.Status == WorkSharingOperationStatus.Succeeded));
        Assert.IsTrue(results.Any(value => value.Value!.Attempts.Count == 2));
        await first.ReopenAsync(); left = SharingService(first, remote); await left.InitializeAsync(default);
        var replay = await left.PublishAsync(first.Workspace, lp.Id, one, default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, replay.Value!.Status);
        var pulled = await left.PullAsync(first.Workspace, lp.Id, null, default);
        Assert.HasCount(2, pulled.Value!.Records); Assert.AreEqual(2, remote.Publications);
        var changed = one with { Changes = [one.Changes[0] with { Content = JsonSerializer.SerializeToElement(one.Changes[0].Content.Deserialize<WorkSharingContribution>(SharingJson)! with { Evidence = "Changed intent" }, SharingJson) }] };
        Assert.AreEqual("sharing_command_conflict", (await left.PublishAsync(first.Workspace, lp.Id, changed, default)).ErrorCode);
    }

    [TestMethod]
    public async Task SharingLostPublicationResponseRecoversWithoutResubmittingAndExposesPendingHistory()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider { LoseResponse = true };
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var command = new PublishWorkSharingCommand(new(Guid.NewGuid()), project.Version, [ContributionChange()]);
        var first = await service.PublishAsync(fixture.Workspace, project.Id, command, default);
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, first.Value!.Status);
        await fixture.ReopenAsync(); service = SharingService(fixture, remote); await service.InitializeAsync(default);
        var recovered = await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, recovered.Value!.Status); Assert.AreEqual(1, remote.Publications);
        var page = await service.ListOperationsAsync(fixture.Workspace, project.Id, 0, 1, default);
        Assert.HasCount(1, page.Value!.Items); Assert.IsFalse(page.Value.HasMore);
    }

    [TestMethod]
    public async Task SharingConflictingRecordVersionsAndImmutableContributionEditsNeverReplaceRemoteContent()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider();
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var contribution = ContributionChange();
        var created = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [contribution]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, created.Value!.Status);
        var content = contribution.Content.Deserialize<WorkSharingContribution>(SharingJson)! with { Evidence = "Conflicting evidence" };
        var edit = contribution with { ExpectedVersion = 1, Content = JsonSerializer.SerializeToElement(content, SharingJson) };
        var conflict = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [edit]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Conflict, conflict.Value!.Status); Assert.AreEqual("sharing_record_conflict", conflict.Value.ErrorCode);
        Assert.AreEqual(1, remote.Publications);
        var duplicate = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [contribution]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, duplicate.Value!.Status); Assert.AreEqual(1, remote.Publications);
    }

    [TestMethod]
    public async Task SharingAdmissionRequiresCurrentAllocationAndCannotOmitTheMappedProject()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider(); var participant = new WorkParticipantId(Guid.NewGuid());
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), participant);
        var tree = await fixture.CreateTreeAsync(); var view = await ActiveAsync(fixture, tree);
        project = ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { PlanIds = [view.Plan.Id] }), default));
        var allocation = tree.Tasks.Where(task => task.Kind == WorkTaskKind.Atomic).Select(task => new WorkTaskAllocation(task.Id, participant)).ToArray();
        var portable = (await service.ExportPlanAsync(fixture.Workspace, view.Plan.Id, allocation, default)).Value!;
        var change = new WorkSharingChange(new(WorkSharingDomain.Plan, view.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(portable.Plan, SharingJson));
        Assert.IsTrue((await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [change]), default)).IsSuccess);
        var gate = new WorkProjectSharingAdmissionGate(service); var scope = new FlowRunScope(Guid.NewGuid(), fixture.Workspace, fixture.Principal);
        var taskId = allocation[0].TaskId;
        Assert.AreEqual("sharing_project_required", (await gate.ObserveAsync(scope, view.Plan, view.Revision, taskId, default)).ErrorCode);
        var plan = view.Plan with { Execution = new(scope, view.Revision.Number, view.Revision.Digest, true, []) { ProjectId = project.Id } };
        var observed = await gate.ObserveAsync(scope, plan, view.Revision, taskId, default);
        Assert.IsTrue(observed.IsSuccess, observed.ErrorCode); Assert.HasCount(1, observed.Value!);
        var changed = portable.Plan with { Allocation = allocation.Select(value => value with { ParticipantId = new(Guid.NewGuid()) }).ToArray() };
        var publication = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version,
            [change with { ExpectedVersion = 1, Content = JsonSerializer.SerializeToElement(changed, SharingJson) }]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, publication.Value!.Status);
        Assert.AreEqual("sharing_task_not_allocated", (await gate.ObserveAsync(scope, plan, view.Revision, taskId, default)).ErrorCode);
        var stale = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [change]), default);
        Assert.AreEqual(WorkSharingOperationStatus.Conflict, stale.Value!.Status);
    }

    [TestMethod]
    public async Task SharingRejectsUnsupportedCapabilitiesForeignOwnershipAndChangedConnectionBeforeEffects()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider();
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        remote.Supported = false;
        Assert.AreEqual("sharing_capability_missing", (await service.PullAsync(fixture.Workspace, project.Id, null, default)).ErrorCode);
        remote.Supported = true;
        Assert.AreEqual("work_project_not_found", (await service.PullAsync(new(Guid.NewGuid()), project.Id, null, default)).ErrorCode);
        var other = SharingService(fixture, remote, Guid.NewGuid());
        Assert.AreEqual("work_project_not_found", (await other.GetOperationAsync(fixture.Workspace, project.Id, new(Guid.NewGuid()), default)).ErrorCode);
        remote.LoseResponse = true;
        var operation = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [ContributionChange()]), default);
        ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { Sharing = project.Definition.Sharing! with { Location = "different" } }), default));
        Assert.AreEqual("sharing_connection_changed", (await service.ResumeAsync(fixture.Workspace, project.Id, operation.Value!.Id, default)).ErrorCode);
        Assert.AreEqual(1, remote.Publications);
    }

    [TestMethod]
    public async Task SharingExchangesAllPortableDomainsAndRejectsPhysicalIdentityAndCredentialDeclarations()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider(); var participant = new WorkParticipantId(Guid.NewGuid());
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), participant);
        var usage = new WorkSharingUsage(Guid.NewGuid(), null, null, new Dictionary<string, long> { ["tokens"] = 12 });
        var profile = new WorkSharingBootstrapProfile("coding", "en-US", [new("agentstration.io/v1", "Agent", "coder", "default",
            JsonSerializer.SerializeToElement(new { displayName = "Coder", instructions = "Use the local deterministic client." }))]);
        var changes = new WorkSharingChange[]
        {
            new(new(WorkSharingDomain.ProjectMetadata, "project"), 0, JsonSerializer.SerializeToElement(new WorkSharingProjectMetadata("Shared", null, [participant]), SharingJson)),
            new(new(WorkSharingDomain.Usage, usage.Id.ToString("D")), 0, JsonSerializer.SerializeToElement(usage, SharingJson)),
            new(new(WorkSharingDomain.BootstrapProfile, profile.Name), 0, JsonSerializer.SerializeToElement(profile, SharingJson))
        };
        var result = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, changes), default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, result.Value!.Status);
        Assert.HasCount(3, (await service.PullAsync(fixture.Workspace, project.Id, null, default)).Value!.Records);
        foreach (var field in new[] { "workspaceId", "ownerPrincipalId", "credential", "apiKey", "scopeRef" })
        {
            var unsafeProfile = profile with { Resources = [profile.Resources[0] with { Definition = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [field] = "forbidden" }) }] };
            Assert.IsFalse(WorkSharingContent.Normalize(changes[2] with { Content = JsonSerializer.SerializeToElement(unsafeProfile, SharingJson) }).IsSuccess);
        }
        Assert.IsFalse(WorkSharingContent.Normalize(changes[1] with { Content = JsonSerializer.SerializeToElement(usage with { Counters = new Dictionary<string, long> { ["tokens"] = -1 } }, SharingJson) }).IsSuccess);
    }

    [TestMethod]
    public async Task SharingAdmissionGuardsLocalVersionsAndImmutableIntentBeforeAnyRemoteEffect()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider { LoseResponse = true };
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var result = await service.PublishAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), project.Version, [ContributionChange()]), default);
        var operation = result.Value!;
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, operation.Status);
        Assert.IsFalse(await fixture.Sharing.UpdateOperationAsync(operation with { Version = operation.Version + 1, PrincipalId = Guid.NewGuid() }, operation.Version, default));
        Assert.IsFalse(await fixture.Sharing.UpdateOperationAsync(operation with { Version = operation.Version + 1,
            Attempts = [operation.Attempts[0] with { ExpectedRevision = "tampered" }] }, operation.Version, default));
        Assert.IsNull(await fixture.Sharing.GetOperationAsync(fixture.Workspace, Guid.NewGuid(), project.Id, operation.Id, default));
        var stale = operation with { Id = new(Guid.NewGuid()), Version = 1, Status = WorkSharingOperationStatus.Pending, Attempts = [] };
        ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { Description = "Operator edit" }), default));
        Assert.AreEqual(WorkSharingSaveStatus.WorkChanged, await fixture.Sharing.AdmitAsync(stale, default));
        Assert.IsNull(await fixture.Sharing.GetOperationAsync(fixture.Workspace, fixture.Principal, project.Id, stale.Id, default));
        Assert.AreEqual(1, remote.Publications);
    }

    [TestMethod]
    public async Task SharingCancellationLeavesRecoverableIntentAndDoesNotReapplyBootstrapOrDuplicateEffects()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider();
        var (service, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        using var cancellation = new CancellationTokenSource();
        remote.BeforePublish = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); };
        var command = new PublishWorkSharingCommand(new(Guid.NewGuid()), project.Version, [ContributionChange()]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PublishAsync(fixture.Workspace, project.Id, command, cancellation.Token));
        var pending = await service.GetOperationAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(WorkSharingOperationStatus.Pending, pending.Value!.Status); Assert.HasCount(1, pending.Value.Attempts);
        Assert.AreEqual(0, remote.Publications);
        await fixture.ReopenAsync(); service = SharingService(fixture, remote); remote.BeforePublish = null;
        var resumed = await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, resumed.Value!.Status); Assert.AreEqual(1, remote.Publications);
    }

    [TestMethod]
    public async Task SharingGateLeavesOrdinaryLocalPlansOfflineAndIgnoresNoConnectorProjects()
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider { Supported = false };
        var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var service = SharingService(fixture, remote); await service.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync(); var view = await ActiveAsync(fixture, tree);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "offline", default,
            new(Guid.NewGuid(), 0, new("Offline", null, [view.Plan.Id], [])), default));
        var gate = new WorkProjectSharingAdmissionGate(service); var scope = new FlowRunScope(Guid.NewGuid(), fixture.Workspace, fixture.Principal);
        var task = view.Revision.Definition.TaskIds[0];
        Assert.IsTrue((await gate.ObserveAsync(scope, view.Plan, view.Revision, task, default)).IsSuccess);
        var plan = view.Plan with { Execution = new(scope, view.Revision.Number, view.Revision.Digest, true, []) { ProjectId = project.Id } };
        Assert.IsTrue((await gate.ObserveAsync(scope, plan, view.Revision, task, default)).IsSuccess);
        Assert.AreEqual(0, remote.Publications);
    }

    private static readonly JsonSerializerOptions SharingJson = new(JsonSerializerDefaults.Web);
    private static WorkSharingChange ContributionChange()
    {
        var value = new WorkSharingContribution(Guid.NewGuid(), null, null, "evidence", "Observed fixture result", []);
        return new(new(WorkSharingDomain.Contribution, value.Id.ToString("D")), 0, JsonSerializer.SerializeToElement(value, SharingJson));
    }
    private static WorkProjectSharingService SharingService(Fixture fixture, SharingProvider remote, Guid? principal = null) => new(
        fixture.Sharing, ProjectService(fixture, owner: principal), fixture.Plans(owner: principal), fixture.Trees(owner: principal), remote,
        new Context(fixture.Workspace, principal ?? fixture.Principal), new Clock(Now), new YamlWorkDocumentCodec());
    private static async Task<(WorkProjectSharingService Service, WorkProjectSnapshot Project)> SharingAsync(Fixture fixture, SharingProvider remote,
        WorkSharingProjectId portableProject, WorkParticipantId participant)
    {
        var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var binding = Binding(fixture) with { Target = Binding(fixture).Target with { Address = new(ResourceScopeRef.Workspace(fixture.Workspace.Value), default, "Tool", "sharing-relay") } };
        var definition = new WorkProjectDefinition("Sharing", null, [], []) { Bindings = [binding], Sharing = new(portableProject, participant, binding.Name, "shared") };
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "sharing", default, new(Guid.NewGuid(), 0, definition), default));
        var service = SharingService(fixture, remote); await service.InitializeAsync(default); return (service, project);
    }
    private sealed class SharingProvider : IWorkProjectSharingGateway
    {
        private readonly object sync = new(); private readonly Dictionary<string, WorkSharingSnapshot> history = []; private readonly Dictionary<string, WorkSharingProviderResult> receipts = [];
        private WorkSharingSnapshot? latest;
        public TwoWriters? Concurrent { get; init; }
        public bool Supported { get; set; } = true;
        public Action<CancellationToken>? BeforePublish { get; set; }
        public bool LoseResponse { get; set; }
        public int Publications { get; private set; }
        public Task<Result<WorkSharingProviderCapabilities>> DiscoverAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding, WorkProjectSharingConnection connection, CancellationToken token)
            => Task.FromResult(Result<WorkSharingProviderCapabilities>.Success(new(WorkSharingFormat.Version, true, true, true, true, Supported, true, true)));
        public Task<Result<WorkSharingSnapshot>> ReadAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding, WorkProjectSharingConnection connection, string? revision, CancellationToken token)
        {
            lock (sync) return Task.FromResult(Result<WorkSharingSnapshot>.Success(revision is null ? latest ?? new(null, new(connection.ProjectId, []), []) : history[revision]));
        }
        public async Task<WorkSharingProviderResult> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkProjectSharingConnection connection, WorkSharingTransportAttempt attempt, bool recover, CancellationToken token)
        {
            BeforePublish?.Invoke(token);
            if (Concurrent is not null && !recover) await Concurrent.EnterAsync();
            await Task.Yield(); token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (receipts.TryGetValue(attempt.OperationKey, out var receipt)) return receipt;
                if (recover) return new(WorkSharingProviderStatus.Missing, attempt.OperationKey, attempt.IntentDigest);
                if (latest?.Revision != attempt.ExpectedRevision) return new(WorkSharingProviderStatus.Conflict, attempt.OperationKey, attempt.IntentDigest);
                var records = (latest?.Records ?? []).ToDictionary(record => record.Key);
                foreach (var record in attempt.Records) records[record.Key] = record;
                var revision = "revision-" + ++Publications;
                latest = new(revision, attempt.Space, records.Values.ToArray()); history[revision] = latest;
                receipt = new(WorkSharingProviderStatus.Succeeded, attempt.OperationKey, attempt.IntentDigest, revision); receipts[attempt.OperationKey] = receipt;
                if (LoseResponse) { LoseResponse = false; return receipt with { Status = WorkSharingProviderStatus.Uncertain }; }
                return receipt;
            }
        }
    }
}
