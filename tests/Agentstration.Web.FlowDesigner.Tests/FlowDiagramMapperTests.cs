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
        Assert.AreSame(projection.NodesByName["input"].Outputs.Single(), ((SinglePortAnchor)projection.Links.Single().Source).Port);
        Assert.AreSame(projection.NodesByName["output"].Input, ((SinglePortAnchor)projection.Links.Single().Target).Port);
    }

    [TestMethod]
    public void PortsOnlyAllowOutputToInputConnectionsBetweenDifferentCompatibleNodes()
    {
        var input = new FlowDiagramNode(new("input", "input", "Input", new(0, 0), null, ["completed"]));
        var transform = new FlowDiagramNode(new("transform", "transform", "Transform", new(200, 0), null, ["completed"]));
        var output = new FlowDiagramNode(new("output", "output", "Output", new(400, 0), null, []));

        Assert.IsTrue(input.Outputs.Single().CanAttachTo(transform.Input));
        Assert.IsTrue(transform.Input.CanAttachTo(input.Outputs.Single()));
        Assert.IsFalse(input.Outputs.Single().CanAttachTo(transform.Outputs.Single()));
        Assert.IsFalse(transform.Input.CanAttachTo(transform.Outputs.Single()));
        Assert.IsFalse(transform.Outputs.Single().CanAttachTo(input.Input));
        Assert.IsEmpty(output.Outputs);

        input.Locked = true;
        Assert.IsFalse(input.Outputs.Single().CanAttachTo(transform.Input));
    }

    [TestMethod]
    public void ProjectCreatesSemanticOutputPortsAndBindsLinksToTheirEvent()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "condition",
            Steps =
            [
                new ConditionFlowStepDefinition { Name = "condition", Left = "${input.ready}", Operator = "equals", Right = "true" },
                new OutputFlowStepDefinition { Name = "accepted" },
                new FailureFlowStepDefinition { Name = "rejected" }
            ],
            Transitions =
            [
                new("accepted", "condition", "true", "accepted"),
                new("rejected", "condition", "false", "rejected")
            ]
        };

        var projection = FlowDiagramMapper.Project(FlowDesignerDocument.From(definition));
        var condition = projection.NodesByName["condition"];

        CollectionAssert.AreEqual(new[] { "true", "false" }, condition.Outputs.Select(port => port.EventName).ToArray());
        Assert.AreSame(condition.Outputs[0], ((SinglePortAnchor)projection.Links.Single(link => link.Id == "accepted").Source).Port);
        Assert.AreSame(condition.Outputs[1], ((SinglePortAnchor)projection.Links.Single(link => link.Id == "rejected").Source).Port);
    }

    [TestMethod]
    public void ProjectDoesNotInventFlowCallPortsFromUnresolvedTransitions()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("child-flow") },
                new OutputFlowStepDefinition { Name = "approved" }
            ],
            Transitions = [new("approved", "child", "approved", "approved")]
        };

        var projection = FlowDiagramMapper.Project(FlowDesignerDocument.From(definition));

        Assert.HasCount(0, projection.NodesByName["child"].Outputs);
        Assert.HasCount(0, projection.Links);
    }

    [TestMethod]
    public void ProjectCreatesSuccessAndErrorPortsForAgentAndToolSteps()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "agent",
            Steps =
            [
                new AgentFlowStepDefinition { Name = "agent", Agent = new("assistant") },
                new ToolFlowStepDefinition { Name = "tool", Tool = new("notification.send") }
            ]
        };

        var document = FlowDesignerDocument.From(definition);

        CollectionAssert.AreEqual(new[] { "success", "error" }, document.Nodes.Single(node => node.Name == "agent").OutputEvents.ToArray());
        CollectionAssert.AreEqual(new[] { "success", "error" }, document.Nodes.Single(node => node.Name == "tool").OutputEvents.ToArray());
    }
}
