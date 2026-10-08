using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AgentstrationDocumentationBootstrapProfileTests
{
    [TestMethod]
    public async Task ProfileCrawlsTheSiteIntoAGovernedKnowledgeSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var profilesPath = Path.Combine(repositoryRoot, "deploy", "bootstrap", "profiles");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = profilesPath
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(repositoryRoot));

        var snapshot = await catalog.GetSnapshotAsync(default);
        var profile = snapshot.Profiles.Single(value => value.Name == "agentstration-documentation");

        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Workspace, profile.Scope);
        Assert.AreEqual(2, profile.ResourceCount);
        Assert.AreEqual(0, profile.Bindings.Count(value => value.TargetKind == BootstrapBindingTargetKind.Tool));
        Assert.IsEmpty(profile.Bindings);

        var loaded = (await catalog.LoadAsync([profile.Name], default)).Single();
        var resources = loaded.Resources.Select(value => value.Resource).ToArray();
        Assert.HasCount(2, resources);
        Assert.AreEqual(1, resources.Count(value => value.Kind == "DataSource"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "KnowledgeSource"));

        var dataSource = resources.Single(value => value.Kind == "DataSource");
        Assert.AreEqual("crawl4ai-web", dataSource.Definition.GetProperty("profile").GetProperty("name").GetString());
        var sourceConfiguration = dataSource.Definition.GetProperty("configuration");
        Assert.AreEqual("https://docs.agentstration.io/", sourceConfiguration.GetProperty("url").GetString());
        Assert.AreEqual(3, sourceConfiguration.GetProperty("maximumDepth").GetInt32());
        Assert.AreEqual(25, sourceConfiguration.GetProperty("maximumPages").GetInt32());

        var source = resources.Single(value => value.Kind == "KnowledgeSource");
        Assert.AreEqual("agentstration-documentation", source.Definition.GetProperty("dataSources")[0]
            .GetProperty("dataSource").GetProperty("name").GetString());

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
