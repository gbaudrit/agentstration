using Agentstration.Flow;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Core.Anchors;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowDiagramMapperTests
{
    [TestMethod]
    public void ProjectPreservesNodePositionsAndTransitionEndpoints()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input", DisplayName = "Prompt" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("done", "input", "completed", "output")],
            Designer = new FlowDesignerMetadata { NodePositions = new Dictionary<string, FlowNodePosition> { ["input"] = new(12, 34), ["output"] = new(200, 34) } }
        };

        var projection = FlowDiagramMapper.Project(FlowDesignerDocument.From(definition));

        Assert.AreEqual(2, projection.Nodes.Count);
        Assert.AreEqual(12d, projection.NodesByName["input"].Position.X);
        Assert.AreEqual("Prompt", projection.NodesByName["input"].Title);
        Assert.AreEqual("done", projection.Links.Single().Id);
        Assert.AreSame(projection.NodesByName["input"].Output, ((SinglePortAnchor)projection.Links.Single().Source).Port);
        Assert.AreSame(projection.NodesByName["output"].Input, ((SinglePortAnchor)projection.Links.Single().Target).Port);
    }
}
