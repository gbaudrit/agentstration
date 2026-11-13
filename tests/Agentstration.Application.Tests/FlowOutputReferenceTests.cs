using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowOutputReferenceTests
{
    [TestMethod]
    [DataRow("${steps.transform.output}", false)]
    [DataRow("${steps.input.output}", true)]
    [DataRow("${transition.output}", true)]
    public async Task OutputMappingsValidateTheirReferencedStep(string mapping, bool valid)
    {
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" },
            new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success, OutputMapping = JsonSerializer.SerializeToElement(mapping) }],
            Transitions = [new("done", "input", "completed", "completed")] };
        var result = await new FlowGraphValidator(new Resolver()).ValidateAsync(graph, new(ResolveResources: false), CancellationToken.None);
        Assert.AreEqual(valid, !result.Issues.Any(issue => issue.Code == "expression_step_unavailable"));
        if (!valid) Assert.IsTrue(result.Issues.Any(issue => issue.StepId == "completed" && issue.PropertyPath == "outputMapping"));
    }
    private sealed class Resolver : IFlowResourceReferenceResolver
    {
        public Task<bool> ExistsAsync(string resourceId, ResourceNamespace? @namespace, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
