using Agentstration.Application.Work;
using Agentstration.Infrastructure.Work;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task YamlDataImportCreatesOnlyDraftWorkAndReplaysAfterRestartAndFileMoves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, project) = await DataFixtureAsync(fixture);
        var id = WorkPlanId.New(); var task = new WorkTaskId(Guid.NewGuid());
        var package = DataPackage(new(Guid.NewGuid()), [(id, task)]);
        var preview = await service.PreviewAsync(fixture.Workspace, project.Id, new(package), default);
        Assert.IsTrue(preview.IsSuccess, preview.ErrorCode); Assert.IsTrue(preview.Value!.CanImport, string.Join(',', preview.Value.Diagnostics));
        Assert.IsFalse((await fixture.Plans().GetAsync(fixture.Workspace, id, default)).IsSuccess);
        var command = new ImportWorkDataCommand(new(Guid.NewGuid()), new(package), preview.Value.Digest!);
        var imported = await service.ImportAsync(fixture.Workspace, project.Id, command, default);
        Assert.IsTrue(imported.IsSuccess, imported.ErrorCode); Assert.AreEqual(WorkDataImportStatus.Succeeded, imported.Value!.Status, imported.Value.ErrorCode);
        var plan = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        Assert.AreEqual(WorkPlanStatus.Draft, plan.Plan.Status); Assert.IsNull(plan.Plan.Execution); Assert.HasCount(0, plan.Plan.Attempts);
        await fixture.ReopenAsync(); (service, _) = await DataFixtureAsync(fixture, project.Id);
        var replay = await service.ImportAsync(fixture.Workspace, project.Id, command with { Source = new(package with { Files = package.Files.Reverse().ToArray() }) }, default);
        Assert.AreEqual(imported.Value.Version, replay.Value!.Version);
        var moved = DataPackage(new(Guid.NewGuid()), [(id, task)], "moved");
        var review = (await service.PreviewAsync(fixture.Workspace, project.Id, new(moved), default)).Value!;
        Assert.IsTrue(review.CanImport, string.Join(',', review.Diagnostics));
        var repeated = await service.ImportAsync(fixture.Workspace, project.Id, new(new(Guid.NewGuid()), new(moved), review.Digest!), default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, repeated.Value!.Status, repeated.Value.ErrorCode);
        Assert.AreEqual(plan.Plan.Version, (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Plan.Version);
        Assert.HasCount(1, (await ProjectService(fixture).GetAsync(fixture.Workspace, project.Id, default)).Value!.Definition.PlanIds);
    }

    [TestMethod]
    public async Task YamlDataReviewRejectsChangedContentLocalEditsAndForeignTargetsBeforeCreation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, project) = await DataFixtureAsync(fixture);
        var id = WorkPlanId.New(); var task = new WorkTaskId(Guid.NewGuid()); var package = DataPackage(new(Guid.NewGuid()), [(id, task)]);
        var preview = (await service.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).Value!;
        var command = new ImportWorkDataCommand(new(Guid.NewGuid()), new(package), preview.Digest!);
        var changed = package with { Files = package.Files.Select(file => file with { Content = file.Content.Replace("Implement fixture", "Changed instruction", StringComparison.Ordinal) }).ToArray() };
        Assert.AreEqual("work_data_review_stale", (await service.ImportAsync(fixture.Workspace, project.Id, command with { Source = new(changed) }, default)).ErrorCode);
        var updated = await ProjectService(fixture).UpdateAsync(fixture.Workspace, project.Id,
            new(Guid.NewGuid(), project.Version, project.Definition with { DisplayName = "Edited locally" }), default);
        Assert.IsTrue(updated.IsSuccess, updated.ErrorCode);
        Assert.AreEqual("work_data_review_stale", (await service.ImportAsync(fixture.Workspace, project.Id, command, default)).ErrorCode);
        Assert.IsFalse((await fixture.Plans().GetAsync(fixture.Workspace, id, default)).IsSuccess);
        Assert.AreEqual("work_project_not_found", (await service.PreviewAsync(new(Guid.NewGuid()), project.Id, new(package), default)).ErrorCode);
        var stranger = DataService(fixture, principal: Guid.NewGuid());
        Assert.AreEqual("work_project_not_found", (await stranger.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).ErrorCode);
    }

    [TestMethod]
    public async Task YamlDataPartialCancellationRetainsProgressAndResumesWithoutRepeatingImportedPlans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, project) = await DataFixtureAsync(fixture);
        var one = WorkPlanId.New(); var two = WorkPlanId.New();
        var package = DataPackage(new(Guid.NewGuid()), [(one, new(Guid.NewGuid())), (two, new(Guid.NewGuid()))]);
        var preview = (await service.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).Value!;
        using var cancellation = new CancellationTokenSource();
        var interrupted = DataService(fixture, new CancelAfterFirstImport(fixture.Data, cancellation));
        var command = new ImportWorkDataCommand(new(Guid.NewGuid()), new(package), preview.Digest!);
        var partial = await interrupted.ImportAsync(fixture.Workspace, project.Id, command, cancellation.Token);
        Assert.IsTrue(partial.IsSuccess, partial.ErrorCode); Assert.AreEqual(WorkDataImportStatus.Partial, partial.Value!.Status);
        Assert.HasCount(1, partial.Value.ImportedPlans); Assert.AreEqual("work_data_import_cancelled", partial.Value.ErrorCode);
        var firstVersion = (await fixture.Plans().GetAsync(fixture.Workspace, one, default)).Value!.Plan.Version;
        await fixture.ReopenAsync(); (service, _) = await DataFixtureAsync(fixture, project.Id);
        var resumed = await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(WorkDataImportStatus.Succeeded, resumed.Value!.Status, resumed.Value.ErrorCode); Assert.HasCount(2, resumed.Value.ImportedPlans);
        Assert.AreEqual(firstVersion, (await fixture.Plans().GetAsync(fixture.Workspace, one, default)).Value!.Plan.Version);
        Assert.AreEqual("work_data_command_conflict", (await service.ImportAsync(fixture.Workspace, project.Id,
            command with { Source = new(package with { Revision = "different" }) }, default)).ErrorCode);
    }

    [TestMethod]
    public async Task YamlDataResumeRefusesPlanEditedAfterCreationBeforeProgressCheckpoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (service, project) = await DataFixtureAsync(fixture);
        var id = WorkPlanId.New(); var task = new WorkTaskId(Guid.NewGuid());
        var package = DataPackage(new(Guid.NewGuid()), [(id, task)]);
        var preview = (await service.PreviewAsync(fixture.Workspace, project.Id, new(package), default)).Value!;
        var command = new ImportWorkDataCommand(new(Guid.NewGuid()), new(package), preview.Digest!);
        var interrupted = DataService(fixture, new LoseFirstImportProgress(fixture.Data));
        Assert.AreEqual("work_data_operation_conflict", (await interrupted.ImportAsync(fixture.Workspace, project.Id, command, default)).ErrorCode);
        var local = (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!;
        var edited = await fixture.Plans().ReviseAsync(fixture.Workspace, id,
            new(Guid.NewGuid(), local.Plan.Version, local.Revision.Definition with { Intent = "Edited after interrupted creation" }), default);
        Assert.IsTrue(edited.IsSuccess, edited.ErrorCode);
        await fixture.ReopenAsync(); (service, _) = await DataFixtureAsync(fixture, project.Id);
        var resumed = await service.ResumeAsync(fixture.Workspace, project.Id, command.Id, default);
        Assert.AreEqual(WorkDataImportStatus.Partial, resumed.Value!.Status);
        Assert.AreEqual("sharing_local_plan_conflict", resumed.Value.ErrorCode);
        Assert.HasCount(0, resumed.Value.ImportedPlans);
        Assert.AreEqual(edited.Value!.Plan.Version, (await fixture.Plans().GetAsync(fixture.Workspace, id, default)).Value!.Plan.Version);
    }

    [TestMethod]
    [DataRow("../outside.yaml")]
    [DataRow("/outside.yaml")]
    [DataRow("flows/coding.yaml")]
    public void YamlDataReaderRejectsUnsafeOrConfigurationPlanReferences(string path)
    {
        var package = DataPackage(new(Guid.NewGuid()), [(WorkPlanId.New(), new(Guid.NewGuid()))]);
        var catalog = new WorkDataManifest<WorkDataCatalogDefinition>(ResourceApiVersions.CoreV1, "WorkCatalog", new("fixture"), new(WorkDataFormat.Version, new(Guid.NewGuid()), [path]));
        package = package with { Files = package.Files.Select(file => file.Path == WorkDataFormat.CatalogPath
            ? file with { Content = ResourceManifestSerializer.ToYaml(catalog) } : file).ToArray() };
        var read = new WorkDataReader(new YamlWorkDocumentCodec()).Read(package);
        Assert.IsNull(read.Selection); Assert.IsTrue(read.Diagnostics.Any(value => value.Code == "work_data_plan_path_invalid"));
    }

    private static WorkDataPackage DataPackage(WorkSharingProjectId portable, IReadOnlyList<(WorkPlanId Plan, WorkTaskId Task)> items, string folder = "fixture")
    {
        var files = new List<WorkDataFile>(); var paths = new List<string>();
        foreach (var (id, task) in items)
        {
            var path = $"plans/{folder}-{id}/plan.yaml"; var treePath = $"plans/{folder}-{id}/taches/tree.yaml";
            paths.Add(path);
            files.Add(new(path, ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkDataPlanDefinition>(ResourceApiVersions.CoreV1,
                "WorkPlan", new("fixture-plan"), new(id, new("Implement fixture", [task], []), [treePath])))));
            files.Add(new(treePath, ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkTaskTreeDefinition>(ResourceApiVersions.CoreV1,
                "WorkTaskTree", new("fixture-tree"), new(task, [new(task, WorkTaskKind.Atomic, "Implement fixture", "Implement fixture")])))));
        }
        files.Add(new(WorkDataFormat.CatalogPath, ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkDataCatalogDefinition>(ResourceApiVersions.CoreV1,
            "WorkCatalog", new("fixture"), new(WorkDataFormat.Version, portable, paths)))));
        return new(null, files);
    }

    private static WorkDataImportService DataService(Fixture fixture, IWorkDataRepository? store = null, Guid? principal = null) =>
        new(store ?? fixture.Data, ProjectService(fixture, owner: principal), fixture.Plans(owner: principal), fixture.Trees(owner: principal), fixture.Sharing,
            new NoDataProvider(), new(new YamlWorkDocumentCodec()), OrganizationService(fixture, principal), new Context(fixture.Workspace, principal ?? fixture.Principal), new Clock(Now));

    private static async Task<(WorkDataImportService Service, WorkProjectSnapshot Project)> DataFixtureAsync(Fixture fixture, WorkProjectId? id = null)
    {
        var projects = ProjectService(fixture); await projects.InitializeAsync(default); await fixture.Sharing.InitializeAsync(default);
        var service = DataService(fixture); await service.InitializeAsync(default);
        var project = id is { } existing ? await projects.GetAsync(fixture.Workspace, existing, default)
            : await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "data", default, new(Guid.NewGuid(), 0, new("Data", null, [], [])), default);
        Assert.IsTrue(project.IsSuccess, project.ErrorCode); return (service, project.Value!);
    }

    private sealed class NoDataProvider : IWorkProjectDataGateway
    {
        public Task<Result<WorkDataPackage>> ReadDataAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkProjectSharingConnection connection, string? revision, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Uploaded/local work must not require a provider.");
    }

    private sealed class LoseFirstImportProgress(IWorkDataRepository inner) : IWorkDataRepository
    {
        private bool lost;
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<WorkOrganizationSnapshot?> GetOrganizationAsync(WorkspaceId workspace, Guid owner, WorkProjectId project, CancellationToken token) => inner.GetOrganizationAsync(workspace, owner, project, token);
        public Task<bool> SaveOrganizationAsync(WorkOrganizationSnapshot value, long storageVersion, long projectVersion, CancellationToken token) => inner.SaveOrganizationAsync(value, storageVersion, projectVersion, token);
        public Task<WorkDataImport?> GetImportAsync(WorkspaceId workspace, Guid owner, WorkProjectId project, WorkDataImportId id, CancellationToken token) => inner.GetImportAsync(workspace, owner, project, id, token);
        public Task<bool> SaveImportAsync(WorkDataImport operation, long version, CancellationToken token)
        {
            if (!lost && operation.ImportedPlans.Count == 1) { lost = true; return Task.FromResult(false); }
            return inner.SaveImportAsync(operation, version, token);
        }
    }

    private sealed class CancelAfterFirstImport(IWorkDataRepository inner, CancellationTokenSource cancellation) : IWorkDataRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public Task<WorkOrganizationSnapshot?> GetOrganizationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, CancellationToken cancellationToken) => inner.GetOrganizationAsync(workspaceId, principalId, projectId, cancellationToken);
        public Task<bool> SaveOrganizationAsync(WorkOrganizationSnapshot value, long expectedStorageVersion, long expectedProjectVersion, CancellationToken cancellationToken) => inner.SaveOrganizationAsync(value, expectedStorageVersion, expectedProjectVersion, cancellationToken);
        public Task<WorkDataImport?> GetImportAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkDataImportId id, CancellationToken cancellationToken) =>
            inner.GetImportAsync(workspaceId, principalId, projectId, id, cancellationToken);
        public async Task<bool> SaveImportAsync(WorkDataImport operation, long expectedVersion, CancellationToken cancellationToken)
        {
            var saved = await inner.SaveImportAsync(operation, expectedVersion, cancellationToken);
            if (saved && operation.ImportedPlans.Count == 1 && operation.Status == WorkDataImportStatus.Pending) cancellation.Cancel();
            return saved;
        }
    }
}
