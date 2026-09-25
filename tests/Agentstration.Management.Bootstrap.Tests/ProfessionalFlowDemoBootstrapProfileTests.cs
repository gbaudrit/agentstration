using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ProfessionalFlowDemoBootstrapProfileTests
{
    private const string ProfileName = "professional-flow-demo";
    private const string ResourceNamespace = "demo.flows-professionnels";

    [TestMethod]
    public async Task ProfileDeclaresComposableFlowsForAnExistingWorkspace()
    {
        var repositoryRoot = FindRepositoryRoot();
        var profilesPath = Path.Combine(repositoryRoot, "deploy", "bootstrap", "profiles");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = profilesPath
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(repositoryRoot));

        var snapshot = await catalog.GetSnapshotAsync(default);
        var profile = snapshot.Profiles.Single(value => value.Name == ProfileName);

        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Workspace, profile.Scope);
        Assert.AreEqual(13, profile.ResourceCount);
        CollectionAssert.AreEquivalent(
            new[] { "demo-model", "demo-runtime" },
            profile.Bindings.Select(value => value.Name).ToArray());

        var loaded = (await catalog.LoadAsync([ProfileName], default)).Single();
        var resources = loaded.Resources.Select(value => value.Resource).ToArray();

        Assert.HasCount(13, resources);
        Assert.AreEqual(0, resources.Count(value => value.Kind == "Workspace"));
        Assert.AreEqual(4, resources.Count(value => value.Kind == "Agent"));
        Assert.AreEqual(7, resources.Count(value => value.Kind == "Flow"));
        Assert.AreEqual(2, resources.Count(value => value.Kind == "Entry"));
        Assert.IsTrue(resources.All(value => value.Metadata.Namespace.Value == ResourceNamespace));
        Assert.AreEqual(
            resources.Length,
            resources.Select(value => (value.Kind, value.Metadata.Namespace.Value, value.Metadata.Name)).Distinct().Count());

        CollectionAssert.AreEquivalent(
            new[]
            {
                "analyste-reunion",
                "analyste-incident",
                "analyste-actions",
                "redacteur-professionnel"
            },
            resources.Where(value => value.Kind == "Agent").Select(value => value.Metadata.Name).ToArray());
        Assert.IsTrue(resources
            .Where(value => value.Kind == "Agent")
            .All(value => value.Definition.GetProperty("modelProfile").GetProperty("binding").GetString() == "demo-model"));
        Assert.IsTrue(resources
            .Where(value => value.Kind == "Agent")
            .All(value => value.Definition.GetProperty("runtimeProfile").GetProperty("binding").GetString() == "demo-runtime"));

        var flows = resources.Where(value => value.Kind == "Flow").ToDictionary(value => value.Metadata.Name);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "identifier-actions",
                "extraire-actions",
                "creer-taches",
                "generer-compte-rendu",
                "preparer-communication",
                "traiter-reunion",
                "traiter-incident"
            },
            flows.Keys.ToArray());

        foreach (var reference in flows.Values.SelectMany(FlowReferences))
        {
            Assert.AreEqual(ResourceNamespace, reference.Namespace);
            Assert.IsTrue(flows.ContainsKey(reference.ResourceId), $"Referenced Flow '{reference.ResourceId}' is not declared by the profile.");
        }

        var sharedFlows = new[]
        {
            "extraire-actions",
            "creer-taches",
            "generer-compte-rendu",
            "preparer-communication"
        };
        CollectionAssert.AreEquivalent(sharedFlows, FlowReferences(flows["traiter-reunion"]).Select(value => value.ResourceId).ToArray());
        CollectionAssert.AreEquivalent(sharedFlows, FlowReferences(flows["traiter-incident"]).Select(value => value.ResourceId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "identifier-actions" },
            FlowReferences(flows["extraire-actions"]).Select(value => value.ResourceId).ToArray());

        var entries = resources.Where(value => value.Kind == "Entry").ToDictionary(value => value.Metadata.Name);
        AssertEntry(entries["analyser-reunion"], "traiter-reunion");
        AssertEntry(entries["analyser-incident"], "traiter-incident");
    }

    private static IEnumerable<(string ResourceId, string Namespace)> FlowReferences(BootstrapResourceDocument resource) =>
        resource.Definition.GetProperty("graph").GetProperty("steps").EnumerateArray()
            .Where(step => step.GetProperty("type").GetString() == "flow")
            .Select(step => step.GetProperty("flow"))
            .Select(flow => (
                flow.GetProperty("resourceId").GetString()!,
                flow.GetProperty("namespace").GetString()!));

    private static void AssertEntry(BootstrapResourceDocument entry, string expectedFlow)
    {
        var binding = entry.Definition.GetProperty("binding");
        Assert.AreEqual("flow", binding.GetProperty("kind").GetString());
        Assert.AreEqual(expectedFlow, binding.GetProperty("resourceId").GetString());
        Assert.AreEqual(ResourceNamespace, binding.GetProperty("namespace").GetString());
        Assert.IsTrue(entry.Definition.GetProperty("presentation").GetProperty("suggestions").GetArrayLength() > 0);
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
