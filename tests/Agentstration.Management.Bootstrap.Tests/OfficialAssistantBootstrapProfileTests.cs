using System.Text.Json;
using Agentstration.Flows;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class OfficialAssistantBootstrapProfileTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        AssertFlowTransitionsMatchDeclaredOutputs(resources);
        var entry = resources.Single(value => value.Kind == "Entry" && value.Metadata.Name == "ask-agentstration");
        Assert.AreEqual("agentstration.assistant", entry.Definition.GetProperty("binding").GetProperty("namespace").GetString());
    }

    private static void AssertFlowTransitionsMatchDeclaredOutputs(IReadOnlyCollection<BootstrapResourceDocument> resources)
    {
        var flows = resources
            .Where(resource => resource.Kind == "Flow")
            .Select(resource => new
            {
                Id = $"{resource.Metadata.Namespace.Value}/{resource.Metadata.Name}",
                Namespace = resource.Metadata.Namespace,
                Graph = resource.Definition.GetProperty("graph").Deserialize<FlowGraphDefinition>(JsonOptions)
                    ?? throw new InvalidOperationException($"Flow '{resource.Metadata.Name}' graph could not be deserialized.")
            })
            .ToArray();
        var outputsByFlow = flows.ToDictionary(
            flow => flow.Id,
            flow => flow.Graph.GetOutputs().Select(output => output.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var flow in flows)
        {
            var graph = flow.Graph;
            Assert.IsFalse(graph.Steps.OfType<FailureFlowStepDefinition>().Any(), $"Flow '{flow.Id}' still uses a legacy failure terminal.");
            Assert.IsTrue(
                graph.Steps.OfType<OutputFlowStepDefinition>().All(output => output.Outcome is not null),
                $"Every output in Flow '{flow.Id}' must declare its outcome.");

            foreach (var call in graph.Steps.OfType<FlowCallStepDefinition>())
            {
                var targetNamespace = call.Flow.Namespace ?? flow.Namespace;
                var targetId = $"{targetNamespace.Value}/{call.Flow.ResourceId}";
                Assert.IsTrue(outputsByFlow.TryGetValue(targetId, out var outputNames), $"FlowCall '{flow.Id}/{call.Name}' targets unknown Flow '{targetId}'.");
                foreach (var transition in graph.Transitions.Where(transition => transition.FromStep == call.Name))
                    Assert.IsTrue(outputNames.Contains(transition.Event), $"Transition '{flow.Id}/{transition.Id}' uses undeclared output '{transition.Event}' from '{targetId}'.");
            }
        }
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
