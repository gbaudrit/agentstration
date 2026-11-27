using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Tests.Work;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task UsageRecoveryAndCorrectionsSurviveLocalServiceRebindingAcrossIndependentParticipants()
    {
        var source = new ProjectContractFixture(); var destination = new ProjectContractFixture();
        using var sourceScope = source.Context.Push(source.Caller); using var destinationScope = destination.Context.Push(destination.Caller);
        await using var left = await Fixture.CreateAsync(source.WorkspaceId, source.PrincipalId);
        await using var right = await Fixture.CreateAsync(destination.WorkspaceId, destination.PrincipalId);
        var remote = new SharingProvider { LoseResponse = true }; var portable = new WorkSharingProjectId(Guid.NewGuid());
        var (_, lp) = await SharingAsync(left, remote, portable, new(Guid.NewGuid()));
        var (other, rp) = await SharingAsync(right, remote, portable, new(Guid.NewGuid()));
        var route = source.AddConnection(true, "tracking"); var otherRoute = destination.AddConnection(true, "tracking", ResourceScopeRef.Instance);
        var account = source.AddService(route, "issues", instanceService: true); var otherAccount = destination.AddService(otherRoute, "issues");
        var replacement = source.AddService(route, "replacement", instanceService: true).Assignment with { Name = "issues" };
        var sourceBindings = new SharingConnectionBindings(source.Resolver); var destinationBindings = new SharingConnectionBindings(destination.Resolver);
        var projects = ProjectService(left, bindings: sourceBindings);
        lp = ProjectSuccess(await projects.UpdateAsync(left.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionRequirements = [account.Requirement], ConnectionAssignments = [account.Assignment] }), default));
        rp = ProjectSuccess(await ProjectService(right, bindings: destinationBindings).UpdateAsync(right.Workspace, rp.Id,
            new(Guid.NewGuid(), rp.Version, rp.Definition with { ConnectionRequirements = [account.Requirement], ConnectionAssignments = [otherAccount.Assignment] }), default));
        Assert.AreNotEqual(account.Assignment.Service.Uid, otherAccount.Assignment.Service.Uid);
        Assert.AreNotEqual(account.Assignment.Service.Address.ScopeRef, otherAccount.Assignment.Service.Address.ScopeRef);
        var entry = UsageEntry(left, lp.Id);
        entry = entry with { Record = entry.Record with { ProjectId = portable, ParticipantId = lp.Definition.Sharing!.ParticipantId } };
        entry = entry with { Digest = WorkUsageFormat.Digest(entry.Record) };
        await left.Usage.InitializeAsync(default); await left.Usage.CaptureAsync([entry], default);
        var service = UsageService(left, remote, new UsageReader());
        var pending = await service.PublishAsync(left.Workspace, lp.Id, entry.Record.Id, default);
        Assert.IsTrue(pending.IsSuccess, pending.ErrorCode); Assert.AreEqual(WorkSharingOperationStatus.Uncertain, pending.Value!.Status);
        lp = ProjectSuccess(await projects.UpdateAsync(left.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionAssignments = [replacement] }), default));
        await left.ReopenAsync(); service = UsageService(left, remote, new UsageReader());
        var recovered = await service.PublishAsync(left.Workspace, lp.Id, entry.Record.Id, default);
        Assert.IsTrue(recovered.IsSuccess, recovered.ErrorCode);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, recovered.Value!.Status);
        Assert.AreEqual(pending.Value.Id, recovered.Value.Id); Assert.AreEqual(pending.Value.IntentDigest, recovered.Value.IntentDigest);
        Assert.AreEqual(1, remote.Publications);
        projects = ProjectService(left, bindings: sourceBindings);
        lp = ProjectSuccess(await projects.UpdateAsync(left.Workspace, lp.Id, new(Guid.NewGuid(), lp.Version,
            lp.Definition with { ConnectionAssignments = [account.Assignment], DisplayName = "Private local edit" }), default));
        var replay = await service.PublishAsync(left.Workspace, lp.Id, entry.Record.Id, default);
        Assert.IsTrue(replay.IsSuccess, replay.ErrorCode); Assert.AreEqual(recovered.Value.Id, replay.Value!.Id);
        var total = FlowRunTokenUsageAggregation.Invocation(21, 6, 31);
        var correction = await service.CorrectAsync(left.Workspace, lp.Id, entry.Record.RootId,
            new(Guid.NewGuid(), entry.Record.Id, entry.Digest, WorkUsageMeasurement.Estimated, new(total, total), "provider_adjustment", "receipt:fixture"), default);
        Assert.IsTrue(correction.IsSuccess, correction.ErrorCode);
        var published = await service.PublishAsync(left.Workspace, lp.Id, correction.Value!.Record.Id, default);
        Assert.IsTrue(published.IsSuccess, published.ErrorCode); Assert.AreEqual(WorkSharingOperationStatus.Succeeded, published.Value!.Status);
        var pulled = await other.PullAsync(right.Workspace, rp.Id, null, default); Assert.IsTrue(pulled.IsSuccess, pulled.ErrorCode);
        var local = (await service.SummarizeAsync(left.Workspace, lp.Id, new([]), default)).Value!;
        var shared = (await UsageService(right, remote, new UsageReader()).SummarizeAsync(right.Workspace, rp.Id, new([], Source: WorkUsageReadSource.Shared), default)).Value!;
        Assert.AreEqual(local.Estimated, shared.Estimated); Assert.AreEqual(31L, shared.Estimated.Inclusive.TotalTokens);
        Assert.AreEqual(1, shared.OriginalRecords); Assert.AreEqual(1, shared.Corrections);
        Assert.AreEqual(1, shared.Estimated.Inclusive.InvocationCount); Assert.IsNull(shared.Measured.Inclusive.TotalTokens);
        Assert.AreEqual(2, remote.Publications);
        var wire = JsonSerializer.Serialize(pulled.Value!.Records);
        foreach (var forbidden in new[] { left.Workspace.ToString(), left.Principal.ToString(), "Private local edit", "connectionAssignments", "scopeRef",
            account.Assignment.Service.Uid.ToString(), replacement.Service.Uid.ToString(), otherAccount.Assignment.Service.Uid.ToString(), route.Contract!.Extension.Uid.ToString() })
            Assert.DoesNotContain(forbidden, wire);
        Assert.HasCount(0, await right.Usage.ReadProjectAsync(right.Workspace, right.Principal, rp.Id, [], 10, default));
    }

    [TestMethod]
    [DataRow("location")]
    [DataRow("binding")]
    public async Task UsageRecoveryCannotRedirectAnUncertainPublicationThroughAnotherSharingConnection(string change)
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider { LoseResponse = true };
        var (_, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var entry = UsageEntry(fixture, project.Id);
        entry = entry with { Record = entry.Record with { ProjectId = project.Definition.Sharing!.ProjectId, ParticipantId = project.Definition.Sharing.ParticipantId } };
        entry = entry with { Digest = WorkUsageFormat.Digest(entry.Record) };
        await fixture.Usage.InitializeAsync(default); await fixture.Usage.CaptureAsync([entry], default);
        var service = UsageService(fixture, remote, new UsageReader());
        var pending = await service.PublishAsync(fixture.Workspace, project.Id, entry.Record.Id, default);
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, pending.Value!.Status);
        var definition = change == "location" ? project.Definition with { Sharing = project.Definition.Sharing! with { Location = "another" } }
            : project.Definition with { Bindings = project.Definition.Bindings.Select(binding => binding with { Target = binding.Target with { Uid = Guid.NewGuid() } }).ToArray() };
        ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version, definition), default));
        Assert.AreEqual("sharing_connection_changed", (await service.PublishAsync(fixture.Workspace, project.Id, entry.Record.Id, default)).ErrorCode);
        Assert.AreEqual(1, remote.Publications);
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, (await SharingService(fixture, remote).GetOperationAsync(fixture.Workspace, project.Id, pending.Value.Id, default)).Value!.Status);
        Assert.AreEqual(entry.Digest, (await service.ListAsync(fixture.Workspace, project.Id, 0, 10, default)).Value!.Items.Single().Digest);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OtherSharingDomainsStillRequireTheirReviewedProjectVersion(bool includeUsage)
    {
        await using var fixture = await Fixture.CreateAsync(); var remote = new SharingProvider { LoseResponse = true };
        var (sharing, project) = await SharingAsync(fixture, remote, new(Guid.NewGuid()), new(Guid.NewGuid()));
        var entry = UsageEntry(fixture, project.Id);
        var record = entry.Record with { ProjectId = project.Definition.Sharing!.ProjectId, ParticipantId = project.Definition.Sharing.ParticipantId };
        var usage = new WorkSharingChange(new(WorkSharingDomain.Usage, record.Id.ToString()), 0,
            JsonSerializer.SerializeToElement(new WorkSharingUsage(record.Id.Value, record.PlanId, record.TaskId, new Dictionary<string, long>())
            { Accounting = record }, SharingJson));
        var pending = await sharing.PublishAsync(fixture.Workspace, project.Id,
            new(new(Guid.NewGuid()), project.Version, includeUsage ? [ContributionChange(), usage] : [ContributionChange()]), default);
        Assert.IsTrue(pending.IsSuccess, pending.ErrorCode); Assert.AreEqual(WorkSharingOperationStatus.Uncertain, pending.Value!.Status);
        ProjectSuccess(await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), project.Version,
            project.Definition with { DisplayName = "Reviewed Project changed" }), default));
        Assert.AreEqual("sharing_connection_changed", (await sharing.ResumeAsync(fixture.Workspace, project.Id, pending.Value.Id, default)).ErrorCode);
        Assert.AreEqual(1, remote.Publications);
    }

}
