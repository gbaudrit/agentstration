using Agentstration.Flows;
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
        Assert.IsTrue(projection.NodesByName["input"].ControlledSize);
        Assert.AreEqual(FlowDiagramNode.RenderedWidth, projection.NodesByName["input"].Size!.Width);
        Assert.AreEqual(FlowDiagramNode.RenderedHeight, projection.NodesByName["input"].Size!.Height);
        Assert.AreEqual("done", projection.Links.Single().Id);
        Assert.AreSame(projection.NodesByName["input"].Output, ((SinglePortAnchor)projection.Links.Single().Source).Port);
        Assert.AreSame(projection.NodesByName["output"].Input, ((SinglePortAnchor)projection.Links.Single().Target).Port);
    }

    [TestMethod]
    public void PortsOnlyAllowOutputToInputConnectionsBetweenDifferentCompatibleNodes()
    {
        var input = new FlowDiagramNode(new("input", "input", "Input", new(0, 0), null));
        var transform = new FlowDiagramNode(new("transform", "transform", "Transform", new(200, 0), null));
        var output = new FlowDiagramNode(new("output", "output", "Output", new(400, 0), null));

        Assert.IsTrue(input.Output.CanAttachTo(transform.Input));
        Assert.IsTrue(transform.Input.CanAttachTo(input.Output));
        Assert.IsFalse(input.Output.CanAttachTo(transform.Output));
        Assert.IsFalse(transform.Input.CanAttachTo(transform.Output));
        Assert.IsFalse(transform.Output.CanAttachTo(input.Input));
        Assert.IsFalse(output.Output.CanAttachTo(transform.Input));
    }
}
