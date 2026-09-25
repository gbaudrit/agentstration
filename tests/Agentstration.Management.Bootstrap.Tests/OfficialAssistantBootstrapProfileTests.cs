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
    public async Task ProfileDeclaresOrderedOrdinaryAssistantResources()
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
        Assert.AreEqual(22, profile.ResourceCount);
        CollectionAssert.AreEquivalent(
            new[] { "assistant-model", "assistant-runtime" },
            profile.Bindings.Select(value => value.Name).ToArray());
        var loaded = (await catalog.LoadAsync(["agentstration-assistant"], default)).Single();
        var resources = loaded.Resources.Select(value => value.Resource).ToArray();
        Assert.HasCount(22, resources);
        Assert.AreEqual(0, resources.Count(value => value.Kind == PackBootstrapKinds.PackInstallation));
        Assert.AreEqual(10, resources.Count(value => value.Kind == "Agent"));
        Assert.AreEqual(11, resources.Count(value => value.Kind == "Flow"));
        Assert.AreEqual(1, resources.Count(value => value.Kind == "Entry"));
        Assert.IsTrue(resources
            .Where(value => value.Metadata.Name.StartsWith("resource-planning", StringComparison.Ordinal))
            .All(value => value.Metadata.Namespace.Value == "agentstration.resource-planning"));
        Assert.IsTrue(resources
            .Where(value => !value.Metadata.Name.StartsWith("resource-planning", StringComparison.Ordinal))
            .All(value => value.Metadata.Namespace.Value == "agentstration.assistant"));
        Assert.IsTrue(resources
            .Where(value => value.Kind == "Agent")
            .All(value => value.Definition.GetProperty("modelProfile").GetProperty("binding").GetString() == "assistant-model"));
        var router = resources.Single(value => value.Kind == "Flow" && value.Metadata.Name == "assistant-router");
        var routerSpec = router.Definition.GetProperty("spec");
        Assert.AreEqual("agentstration.assistant", routerSpec.GetProperty("destinations")[0].GetProperty("namespace").GetString());
        Assert.AreEqual("agentstration.assistant", routerSpec.GetProperty("fallback").GetProperty("namespace").GetString());
        var planningStep = router.Definition.GetProperty("graph").GetProperty("steps")
            .EnumerateArray().Single(value => value.TryGetProperty("name", out var name) && name.GetString() == "resource-planning");
        Assert.AreEqual("agentstration.resource-planning", planningStep.GetProperty("flow").GetProperty("namespace").GetString());
        var entry = resources.Single(value => value.Kind == "Entry" && value.Metadata.Name == "ask-agentstration");
        Assert.AreEqual("agentstration.assistant", entry.Definition.GetProperty("binding").GetProperty("namespace").GetString());
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
