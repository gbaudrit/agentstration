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
        Assert.AreEqual(8, profile.ResourceCount);
        Assert.AreEqual(3, profile.Bindings.Count(value => value.TargetKind == BootstrapBindingTargetKind.Tool));
        Assert.AreEqual(1, profile.Bindings.Count(value => value.TargetKind == BootstrapBindingTargetKind.ModelProfile));
        Assert.AreEqual(1, profile.Bindings.Count(value => value.TargetKind == BootstrapBindingTargetKind.RuntimeProfile));

        var loaded = (await catalog.LoadAsync([profile.Name], default)).Single();
        var resources = loaded.Resources.Select(value => value.Resource).ToArray();
        Assert.HasCount(8, resources);
        Assert.AreEqual(1, resources.Count(value => value.Kind == "ToolSet"));
        Assert.AreEqual(3, resources.Count(value => value.Kind == "Flow"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "KnowledgeSource"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "KnowledgeSourceToolExposure"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "Agent"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "Entry"));

        var source = resources.Single(value => value.Kind == "KnowledgeSource");
        var acquisition = source.Definition.GetProperty("acquisitionConfiguration");
        Assert.AreEqual("https://docs.agentstration.io/", acquisition.GetProperty("url").GetString());
        Assert.AreEqual(3, acquisition.GetProperty("maximumDepth").GetInt32());
        Assert.AreEqual(25, acquisition.GetProperty("maximumPages").GetInt32());

        var ingestion = resources.Single(value => value.Kind == "Flow"
            && value.Metadata.Name == "agentstration-documentation-ingestion");
        var steps = ingestion.Definition.GetProperty("graph").GetProperty("steps").EnumerateArray().ToArray();
        var crawl = steps.Single(value => value.GetProperty("name").GetString() == "crawl");
        Assert.AreEqual("web.crawl", crawl.GetProperty("capability").GetString());
        Assert.AreEqual("${input.sourceConfiguration.url}",
            crawl.GetProperty("argumentsMapping").GetProperty("startUrl").GetString());
        var transfer = steps.Single(value => value.GetProperty("name").GetString() == "transfer");
        Assert.AreEqual("${steps.crawl.output.structuredContent.corpus.reference}",
            transfer.GetProperty("inputMapping").GetProperty("contentReference").GetString());
        var persist = steps.Single(value => value.GetProperty("name").GetString() == "persist");
        Assert.AreEqual("${execution.flowRunId}",
            persist.GetProperty("inputMapping").GetProperty("producerFlowRunId").GetString());

        var assistant = resources.Single(value => value.Kind == "Agent");
        var toolSet = assistant.Definition.GetProperty("toolSets")[0];
        Assert.AreEqual("agentstration-documentation",
            toolSet.GetProperty("toolSet").GetProperty("name").GetString());
        Assert.AreEqual("1.0.0", toolSet.GetProperty("version").GetString());
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
