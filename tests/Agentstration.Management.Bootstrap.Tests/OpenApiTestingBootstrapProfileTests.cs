using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class OpenApiTestingBootstrapProfileTests
{
    [TestMethod]
    public async Task ProfileDeclaresGovernedResourcesAndRequiresProvisionedAepTools()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = Path.Combine(repositoryRoot, "deploy", "bootstrap", "profiles")
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(repositoryRoot));

        var snapshot = await catalog.GetSnapshotAsync(default);
        var profile = snapshot.Profiles.Single(value => value.Name == "openapi-testing");

        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Workspace, profile.Scope);
        Assert.AreEqual(4, profile.ResourceCount);
        Assert.IsFalse(snapshot.InitialProfiles.Contains("openapi-testing", StringComparer.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { "openapi.contract.discover", "openapi.operation.invoke" },
            profile.Bindings.Select(value => value.Name).ToArray());
        Assert.IsTrue(profile.Bindings.All(value => value.Required));
        Assert.IsTrue(profile.Bindings.All(value => value.TargetKind == BootstrapBindingTargetKind.Tool));

        var loaded = (await catalog.LoadAsync(["openapi-testing"], default)).Single();
        var resources = loaded.Resources.Select(value => value.Resource).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "ToolSet", "Flow", "Flow", "DataSourceProfile" },
            resources.Select(value => value.Kind).ToArray());
        Assert.IsTrue(resources
            .Where(value => value.Kind == "Flow")
            .All(value => value.Definition.GetProperty("spec").GetProperty("flowKind").GetString() == "workflow"));
        var discovery = resources.Single(value => value.Kind == "Flow"
            && value.Metadata.Name == "openapi-discovery");
        Assert.AreEqual("1.0.1", discovery.Definition.GetProperty("version").GetString());
        var dataSourceProfile = resources.Single(value => value.Kind == "DataSourceProfile");
        Assert.AreEqual("1.0.1", dataSourceProfile.Definition.GetProperty("discoveryFlow")
            .GetProperty("version").GetString());
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
