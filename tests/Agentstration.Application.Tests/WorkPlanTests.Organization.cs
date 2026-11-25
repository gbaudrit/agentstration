using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.ResourceManagement;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task LogicalEpicsMoveAndUngroupPlansWithoutFunctionalRevisionsOrProviderWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (data, project) = await DataFixtureAsync(fixture);
        var one = WorkPlanId.New(); var two = WorkPlanId.New(); var loose = WorkPlanId.New();
        var package = DataPackage(new(Guid.NewGuid()), [(one, new(Guid.NewGuid())), (two, new(Guid.NewGuid())), (loose, new(Guid.NewGuid()))]);
        var preview = (await data.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).Value!;
        var imported = await data.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(package), preview.Digest!), default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, imported.Value!.Status, imported.Value.ErrorCode);
        var active = (await fixture.Plans().GetAsync(fixture.Workspace, one, default)).Value!;
        active = (await fixture.Plans().TransitionAsync(fixture.Workspace, one, Phase(active, WorkPlanAction.Validate), default)).Value!;
        active = (await fixture.Plans().TransitionAsync(fixture.Workspace, one, Phase(active, WorkPlanAction.Activate), default)).Value!;
        var original = (await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!;
        var remote = new SharingProvider(); var service = OrganizationService(fixture, remote: remote);
        var epic = new WorkEpicId(Guid.NewGuid());
        var definition = new WorkOrganizationDefinition([new(epic, "Library", null, [one, two])]);
        var command = new UpdateWorkOrganizationCommand(Guid.NewGuid(), 0, definition);
        var created = await service.UpdateAsync(fixture.Workspace, project.Id, command, default);
        Assert.IsTrue(created.IsSuccess, created.ErrorCode); Assert.AreEqual(1, created.Value!.Version);
        var noOp = await service.UpdateAsync(fixture.Workspace, project.Id, command with { Id = Guid.NewGuid(), ExpectedVersion = 1 }, default);
        Assert.AreEqual(1, noOp.Value!.Version);
        var moved = new WorkOrganizationDefinition([new(epic, "Library", null, [two]), new(new(Guid.NewGuid()), "Other", null, [one])]);
        Assert.AreEqual("work_organization_conflict", (await service.UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), 0, moved), default)).ErrorCode);
        Assert.AreEqual("work_organization_command_conflict", (await service.UpdateAsync(fixture.Workspace, project.Id, command with { Definition = moved }, default)).ErrorCode);
        var updated = await service.UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), 1, moved), default);
        Assert.AreEqual(2, updated.Value!.Version);
        var cleared = await service.UpdateAsync(fixture.Workspace, project.Id, new(Guid.NewGuid(), 2, new([])), default);
        Assert.AreEqual(3, cleared.Value!.Version); Assert.HasCount(0, cleared.Value.Definition.Epics);
        Assert.AreEqual(original.Version, (await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!.Version);
        foreach (var plan in new[] { one, two, loose })
        {
            var current = (await fixture.Plans().GetAsync(fixture.Workspace, plan, default)).Value!;
            Assert.AreEqual(plan == one ? active.Plan.Version : 1, current.Plan.Version);
            Assert.AreEqual(plan == one ? WorkPlanStatus.Active : WorkPlanStatus.Draft, current.Plan.Status); Assert.IsNull(current.Plan.Execution);
            if (plan == one) Assert.AreEqual(active.Revision.Digest, current.Revision.Digest);
        }
        Assert.AreEqual(0, remote.Publications);
        await fixture.ReopenAsync(); service = OrganizationService(fixture);
        Assert.AreEqual(3, (await service.GetAsync(fixture.Workspace, project.Id, default)).Value!.Version);
        Assert.AreEqual("work_project_not_found", (await OrganizationService(fixture, Guid.NewGuid()).GetAsync(fixture.Workspace, project.Id, default)).ErrorCode);
        Assert.AreEqual("work_organization_plan_not_associated", (await service.UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), 3, new([new(epic, "Foreign", null, [WorkPlanId.New()])])), default)).ErrorCode);
    }

    [TestMethod]
    public async Task YamlEpicImportAndFolderMovesPreservePlanAndOrganizationVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (data, project) = await DataFixtureAsync(fixture);
        var plan = WorkPlanId.New(); var package = EpicPackage(DataPackage(new(Guid.NewGuid()), [(plan, new(Guid.NewGuid()))]), [plan]);
        var preview = (await data.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).Value!;
        Assert.IsTrue(preview.CanImport, string.Join(',', preview.Diagnostics)); Assert.HasCount(1, preview.Organization!.Epics);
        var imported = await data.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(package), preview.Digest!), default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, imported.Value!.Status, imported.Value.ErrorCode);
        var organization = (await OrganizationService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!;
        var moved = package with { Files = package.Files.Select(file => new WorkDataFile(file.Path.Replace("epics/library", "epics/moved", StringComparison.Ordinal),
            file.Content.Replace("epics/library", "epics/moved", StringComparison.Ordinal))).ToArray() };
        var review = (await data.PreviewAsync(fixture.Workspace, project.Id, new(moved), default)).Value!;
        var repeated = await data.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(moved), review.Digest!), default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, repeated.Value!.Status, repeated.Value.ErrorCode);
        Assert.AreEqual(organization.Version, (await OrganizationService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!.Version);
        Assert.AreEqual(1, (await fixture.Plans().GetAsync(fixture.Workspace, plan, default)).Value!.Plan.Version);
        var omission = DataPackage(new(Guid.NewGuid()), [(plan, package.Files.Where(file => file.Path.EndsWith("tree.yaml", StringComparison.Ordinal))
            .Select(file => ResourceManifestSerializer.FromYamlStrict<WorkDataManifest<WorkTaskTreeDefinition>>(file.Content).Definition.RootTaskId).Single())]);
        var plain = (await data.PreviewAsync(fixture.Workspace, project.Id, new(omission), default)).Value!;
        Assert.IsNull(plain.Organization);
        Assert.IsTrue((await data.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(omission), plain.Digest!), default)).IsSuccess);
        Assert.HasCount(1, (await OrganizationService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!.Definition.Epics);
        var files = moved.Files.ToList(); var catalogIndex = files.FindIndex(file => file.Path == WorkDataFormat.CatalogPath);
        var catalog = ResourceManifestSerializer.FromYamlStrict<WorkDataManifest<WorkDataCatalogDefinition>>(files[catalogIndex].Content);
        files[catalogIndex] = files[catalogIndex] with { Content = ResourceManifestSerializer.ToYaml(catalog with { Definition = catalog.Definition with { Plans = [], Epics = [] } }) };
        var clearPackage = moved with { Files = files };
        var clearReview = (await data.PreviewAsync(fixture.Workspace, project.Id, new(clearPackage), default)).Value!;
        Assert.IsTrue(clearReview.CanImport, string.Join(',', clearReview.Diagnostics));
        var clearResult = await data.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(clearPackage), clearReview.Digest!), default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, clearResult.Value!.Status, clearResult.Value.ErrorCode);
        Assert.HasCount(0, clearResult.Value.ImportedPlans);
        Assert.HasCount(0, (await OrganizationService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!.Definition.Epics);
        Assert.AreEqual(1, (await fixture.Plans().GetAsync(fixture.Workspace, plan, default)).Value!.Plan.Version);
    }

    [TestMethod]
    public async Task LogicalOrganizationSharesIndependentlyAndDoesNotReviseTheSharedPlan()
    {
        await using var first = await Fixture.CreateAsync(); await using var second = await Fixture.CreateAsync();
        var remote = new SharingProvider(); var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var (left, lp) = await SharingAsync(first, remote, portable, participant);
        var (right, rp) = await SharingAsync(second, remote, portable, new(Guid.NewGuid()));
        await first.Data.InitializeAsync(default); await second.Data.InitializeAsync(default);
        var plan = WorkPlanId.New(); var task = new WorkTaskId(Guid.NewGuid()); var package = DataPackage(portable, [(plan, task)]);
        var data = DataService(first); var review = (await data.PreviewAsync(first.Workspace, lp.Id, new(package), default)).Value!;
        Assert.AreEqual(WorkDataImportStatus.Succeeded, (await data.ImportAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), new(package), review.Digest!), default)).Value!.Status);
        lp = (await ProjectService(first).GetAsync(first.Workspace, lp.Id, default)).Value!;
        var shared = (await left.ExportPlanAsync(first.Workspace, plan, [new(task, participant)], default)).Value!;
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await left.PublishAsync(first.Workspace, lp.Id, new(new(Guid.NewGuid()), lp.Version,
            [new(new(WorkSharingDomain.Plan, plan.ToString()), 0, JsonSerializer.SerializeToElement(shared.Plan, SharingJson))]), default)).Value!.Status);
        var pulled = (await right.PullAsync(second.Workspace, rp.Id, null, default)).Value!;
        var record = pulled.Records.Single();
        Assert.IsTrue((await right.ImportPlanAsync(second.Workspace, rp.Id, plan, record.Digest, default)).IsSuccess);
        var organization = OrganizationService(first, remote: remote);
        Assert.IsTrue((await organization.UpdateAsync(first.Workspace, lp.Id, new(Guid.NewGuid(), 0,
            new([new(new(Guid.NewGuid()), "Logical epic", null, [plan])])), default)).IsSuccess);
        var published = await organization.PublishAsync(first.Workspace, lp.Id, new(Guid.NewGuid()), 1, 0, default);
        Assert.IsTrue(published.IsSuccess, published.ErrorCode); Assert.AreEqual(WorkSharingOperationStatus.Succeeded, published.Value!.Status, published.Value.ErrorCode);
        var current = (await right.PullAsync(second.Workspace, rp.Id, null, default)).Value!;
        var grouping = current.Records.Single(value => value.Key.Domain == WorkSharingDomain.Organization);
        var adopted = await OrganizationService(second, remote: remote).ImportSharedAsync(second.Workspace, rp.Id, Guid.NewGuid(), 0, grouping.Digest, default);
        Assert.IsTrue(adopted.IsSuccess, adopted.ErrorCode); Assert.HasCount(1, adopted.Value!.Definition.Epics);
        Assert.AreEqual(record.Digest, current.Records.Single(value => value.Key.Domain == WorkSharingDomain.Plan).Digest);
        Assert.AreEqual(record.Version, current.Records.Single(value => value.Key.Domain == WorkSharingDomain.Plan).Version);
        Assert.AreEqual(1, (await second.Plans().GetAsync(second.Workspace, plan, default)).Value!.Plan.Version);
    }

    [TestMethod]
    public void LogicalEpicMembershipCannotDuplicatePlanIdentity()
    {
        var plan = WorkPlanId.New();
        Assert.IsFalse(new WorkOrganizationDefinition([new(new(Guid.NewGuid()), "One", null, [plan]), new(new(Guid.NewGuid()), "Two", null, [plan])]).Validate().IsSuccess);
    }

    private static WorkProjectOrganizationService OrganizationService(Fixture fixture, Guid? principal = null, SharingProvider? remote = null) =>
        new(fixture.Data, ProjectService(fixture, owner: principal), SharingService(fixture, remote ?? new SharingProvider(), principal),
            new Context(fixture.Workspace, principal ?? fixture.Principal), new Clock(Now));

    private static WorkDataPackage EpicPackage(WorkDataPackage package, IReadOnlyList<WorkPlanId> plans)
    {
        var files = package.Files.Select(file => new WorkDataFile(file.Path.Replace("plans/", "epics/library/plans/", StringComparison.Ordinal),
            file.Content.Replace("plans/", "epics/library/plans/", StringComparison.Ordinal))).ToList();
        var index = files.FindIndex(file => file.Path == WorkDataFormat.CatalogPath);
        var catalog = ResourceManifestSerializer.FromYamlStrict<WorkDataManifest<WorkDataCatalogDefinition>>(files[index].Content);
        files[index] = files[index] with { Content = ResourceManifestSerializer.ToYaml(catalog with { Definition = catalog.Definition with { Epics = ["epics/library/epic.yaml"] } }) };
        files.Add(new("epics/library/epic.yaml", ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkEpicDefinition>("agentstration.io/v1", "WorkEpic", new("library"),
            new(new(Guid.NewGuid()), "Library", null, plans)))));
        return package with { Files = files };
    }
}
