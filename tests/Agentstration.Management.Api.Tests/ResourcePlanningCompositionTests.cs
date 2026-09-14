using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourcePlanning;
using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourcePlanningCompositionTests
{
    private static readonly string SampleRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "resource-planning"));

    [TestMethod]
    public void ParentComposesFunctionalChildFlowsAndGovernedHandoff()
    {
        var parent = ResourceManifestSerializer.FromYaml<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(
            File.ReadAllText(Path.Combine(SampleRoot, "flows", "resource-planning.yaml")));
        var calls = parent.Definition.Graph!.Steps.OfType<FlowCallStepDefinition>().ToArray();
        var tools = parent.Definition.Graph.Steps.OfType<ToolFlowStepDefinition>().Select(value => value.Tool.ResourceId).ToArray();

        Assert.HasCount(6, calls);
        CollectionAssert.AreEqual(new[]
        {
            "resource-planning-intent-analysis", "resource-planning-capability-design", "resource-planning-workflow-design",
            "resource-planning-integration-design", "resource-planning-experience-design", "resource-planning-solution-consolidation"
        }, calls.Select(value => value.Flow.ResourceId).ToArray());
        Assert.IsTrue(tools.All(value => value.StartsWith("agentstration.resource-planning.", StringComparison.Ordinal)));
        Assert.IsFalse(tools.Any(value => value.EndsWith(".apply", StringComparison.Ordinal)));
        Assert.IsTrue(parent.Definition.Graph.Steps.OfType<ConditionFlowStepDefinition>().Any(value => value.Name == "ready"));
    }

    [TestMethod]
    public void RepresentativeFunctionalFixtureIsStableAndValid()
    {
        var json = File.ReadAllText(Path.Combine(SampleRoot, "fixtures", "customer-support-plan.json"));
        var plan = JsonSerializer.Deserialize<FunctionalResourcePlanV1>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(plan);
        var content = FunctionalResourcePlanSerializer.Serialize(plan);
        Assert.IsTrue(new FunctionalResourcePlanValidator().Validate(content).IsValid);
        Assert.AreEqual(content.Document.GetRawText(), FunctionalResourcePlanSerializer.Serialize(plan).Document.GetRawText());
    }

    [TestMethod]
    public void SpecialistsAreFunctionalAndCanOnlyUsePlanningTools()
    {
        var agents = Directory.GetFiles(Path.Combine(SampleRoot, "agents"), "*.yaml")
            .Select(path => ResourceManifestSerializer.FromYaml<AgentResource>(File.ReadAllText(path))).ToArray();
        Assert.HasCount(6, agents);
        Assert.IsTrue(agents.All(agent => agent.Definition.Tools.Count > 0));
        Assert.IsTrue(agents.SelectMany(agent => agent.Definition.Tools).All(tool => tool.Name.StartsWith("agentstration.resource-planning.", StringComparison.Ordinal)));
        Assert.IsTrue(agents.All(agent => !agent.Definition.Instructions.Contains("apiVersion:", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(agents.All(agent => !agent.Definition.Instructions.Contains("scopeRef", StringComparison.OrdinalIgnoreCase)));
    }
}
