using System.Net;
using System.Net.Http.Json;
using Agentstration.Application.Work;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Testing;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task ProtectedGitYamlDataIsReviewedThenImportedAsDraftAndReplayedAcrossRestart()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await using var host = await ConnectedHostAsync(native, "yaml-data");
        var portable = new WorkSharingProjectId(Guid.NewGuid());
        var project = await CreateProjectAsync(host, portable, new(Guid.NewGuid()));
        var plan = WorkPlanId.New(); var task = new WorkTaskId(Guid.NewGuid());
        var package = ApiDataPackage(portable, plan, task);
        foreach (var file in package.Files)
        {
            var path = Path.Combine(native.Work, "shared", file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content);
        }
        await native.GitAsync(native.Work, "add", "shared");
        await native.GitAsync(native.Work, "commit", "-m", "Declare YAML Work data fixture");
        await native.GitAsync(native.Work, "push", native.RepositoryPath, "HEAD:refs/heads/main");
        var revision = (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim();
        using var reviewResponse = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/data/preview", new PreviewWorkDataRequest());
        Assert.AreEqual(HttpStatusCode.OK, reviewResponse.StatusCode, await reviewResponse.Content.ReadAsStringAsync());
        var review = (await reviewResponse.Content.ReadFromJsonAsync<WorkDataPreview>())!;
        Assert.IsTrue(review.CanImport, string.Join(',', review.Diagnostics)); Assert.AreEqual(revision, review.Revision);
        using (host.Scope()) Assert.IsFalse((await host.Host.Services.GetRequiredService<WorkPlanService>().GetAsync(new(host.Context.WorkspaceId), plan, default)).IsSuccess);
        var request = new ImportWorkDataRequest(Guid.NewGuid(), review.Digest!, Revision: review.Revision);
        using var accepted = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/data/imports", request);
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        var imported = (await accepted.Content.ReadFromJsonAsync<WorkDataImportResponse>())!;
        Assert.AreEqual(WorkDataImportStatus.Succeeded, imported.Status, imported.ErrorCode); Assert.HasCount(1, imported.ImportedPlans);
        using (host.Scope())
        {
            var draft = (await host.Host.Services.GetRequiredService<WorkPlanService>().GetAsync(new(host.Context.WorkspaceId), plan, default)).Value!;
            Assert.AreEqual(WorkPlanStatus.Draft, draft.Plan.Status); Assert.IsNull(draft.Plan.Execution); Assert.HasCount(0, draft.Plan.Attempts);
        }
        await host.RestartAsync();
        using var replay = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/data/imports", request);
        Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(imported.Version, (await replay.Content.ReadFromJsonAsync<WorkDataImportResponse>())!.Version);
        var receipt = await host.Http.GetFromJsonAsync<WorkDataImportResponse>(host.Root(project.Id) + $"/data/imports/{request.Id}");
        Assert.AreEqual(imported.SourceDigest, receipt!.SourceDigest);
        using var stale = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/data/imports", request with { Id = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await host.Http.GetAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{project.Id}/data/imports/{request.Id}");
        Assert.IsTrue(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.AreEqual(revision, (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim(), "Draft import is not remote publication.");
    }

    private static WorkDataPackage ApiDataPackage(WorkSharingProjectId project, WorkPlanId plan, WorkTaskId task) => new(null,
    [
        new("catalog.yaml", ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkDataCatalogDefinition>(ResourceApiVersions.CoreV1,
            "WorkCatalog", new("fixture"), new(WorkDataFormat.Version, project, ["plans/feature/plan.yaml"])))),
        new("plans/feature/plan.yaml", ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkDataPlanDefinition>(ResourceApiVersions.CoreV1,
            "WorkPlan", new("feature"), new(plan, new("Implement fixture", [task], []), ["plans/feature/taches/tree.yaml"])))),
        new("plans/feature/taches/tree.yaml", ResourceManifestSerializer.ToYaml(new WorkDataManifest<WorkTaskTreeDefinition>(ResourceApiVersions.CoreV1,
            "WorkTaskTree", new("tree"), new(task, [new(task, WorkTaskKind.Atomic, "Implement fixture", "Produce deterministic evidence")]))))
    ]);
}
