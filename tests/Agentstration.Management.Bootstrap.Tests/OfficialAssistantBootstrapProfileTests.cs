using Agentstration.Infrastructure.Packs;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class OfficialAssistantBootstrapProfileTests
{
    [TestMethod]
    public async Task ProfileDeclaresOrderedLocalPackInstallations()
    {
        var repositoryRoot = FindRepositoryRoot();
        var profilesPath = Path.Combine(repositoryRoot, "deploy", "bootstrap", "profiles");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = profilesPath
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(repositoryRoot));

        var snapshot = await catalog.GetSnapshotAsync(default);
        var profile = snapshot.Profiles.Single(value => value.Name == "agentstration-assistant");

        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Workspace, profile.Scope);
        Assert.AreEqual(2, profile.ResourceCount);
        CollectionAssert.AreEquivalent(
            new[] { "assistant-model", "assistant-runtime" },
            profile.Bindings.Select(value => value.Name).ToArray());
        var artifactDirectory = Path.Combine(profilesPath, "agentstration-assistant", "artifacts");
        await using var planningStream = File.OpenRead(Path.Combine(artifactDirectory, "agentstration-resource-planning.zip"));
        await using var assistantStream = File.OpenRead(Path.Combine(artifactDirectory, "agentstration-assistant.zip"));
        var reader = new ZipPackArchiveReader();
        var planning = await reader.ReadAsync(planningStream, "agentstration-resource-planning.zip", default);
        var assistant = await reader.ReadAsync(assistantStream, "agentstration-assistant.zip", default);
        Assert.AreEqual("resource-planning", planning.Manifest.Metadata.Name);
        Assert.AreEqual("assistant", assistant.Manifest.Metadata.Name);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The Agentstration repository root could not be located.");
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Management.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
