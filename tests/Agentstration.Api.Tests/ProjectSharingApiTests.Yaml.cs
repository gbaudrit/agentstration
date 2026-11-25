using System.Net;
using System.Net.Http.Json;
using Agentstration.Testing;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task YamlSharingRefusesAnExistingJsonSpaceWithoutResettingItsHistory()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(native.Work, "shared"));
        await File.WriteAllTextAsync(Path.Combine(native.Work, "shared/index.json"), "{\"formatVersion\":\"1.0\"}");
        await native.GitAsync(native.Work, "add", "shared");
        await native.GitAsync(native.Work, "commit", "-m", "Retain legacy sharing fixture");
        await native.GitAsync(native.Work, "push", native.RepositoryPath, "HEAD:refs/heads/main");
        var revision = await native.GitAsync(native.RepositoryPath, "rev-parse", "main");
        await using var host = await ConnectedHostAsync(native, "legacy");
        var project = await CreateProjectAsync(host, new(Guid.NewGuid()), new(Guid.NewGuid()));
        using var response = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/sharing/pull", new PullWorkSharingRequest());
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sharing_format_upgrade_required", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(revision, await native.GitAsync(native.RepositoryPath, "rev-parse", "main"));
    }
}
