using System.Text.Json;
using Agentstration.Flows;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class FlowDataContractTests
{
    [TestMethod]
    [DataRow("${transition.output}", true)]
    [DataRow("${steps.agent.output}", true)]
    [DataRow("${steps.agent.output.value}", false)]
    public void UnchangedFinalAgentOutputDefinesTheFlowContract(string mapping, bool inherited)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "string" });
        var output = new OutputFlowStepDefinition { Name = "output", Outcome = FlowOutputOutcome.Success, OutputMapping = JsonSerializer.SerializeToElement(mapping) };
        var graph = new FlowGraphDefinition { EntryStep = "agent", Steps = [new AgentFlowStepDefinition { Name = "agent", Agent = new("fixture") { OutputSchema = schema } }, output],
            Transitions = [new("done", "agent", "success", "output")] };
        Assert.AreEqual(inherited, graph.ResolveOutputSchema(output, out _) is not null);
    }
    [TestMethod]
    [DataRow(true, "${transition.output}", false)]
    [DataRow(true, "${steps.child.output.value}", true)]
    [DataRow(false, "${transition.output}", true)]
    public void IntermediateChildrenReceiveTheGoalWithoutBeingForcedToReturnTheFinalShape(bool finalChild, string mapping, bool intermediate)
    {
        var goal = Agentstration.Schemas.DataContracts.Declared(JsonSerializer.SerializeToElement(new { type = "object" }), "final-artifact")!;
        var parent = new Agentstration.Schemas.InvocationDataContracts(ExpectedOutput: goal);
        var graph = new FlowGraphDefinition { EntryStep = "child", Steps = [
            new FlowCallStepDefinition { Name = "child", Flow = new("fixture", Version: "1.0.0") },
            new TransformFlowStepDefinition { Name = "adapt" },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement(mapping) }],
            Transitions = [new("done", "child", "completed", finalChild ? "output" : "adapt")] };
        var context = FlowDataContracts.ChildContext(graph, "child", parent)!;
        Assert.AreEqual(intermediate, context.FinalResultMapped);
        Assert.AreEqual(goal.Digest, context.ExpectedOutput!.Digest);
        var response = FlowDataContracts.AgentOutput(null, "agent", "run", "agent:fixture", null, null, context);
        Assert.AreEqual(!intermediate, response is not null);
        StringAssert.Contains(FlowDataContracts.AgentInstructions("Process", null, response, context), "object");
    }
}
