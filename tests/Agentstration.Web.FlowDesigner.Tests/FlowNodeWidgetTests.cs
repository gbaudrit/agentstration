using Agentstration.Flow;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Components.Renderers;
using Bunit;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowNodeWidgetTests
{
    [TestMethod]
    public void RouterNodeMaterializesItsTypeAndRouteCount()
    {
        using var context = new BunitContext();
        context.ComponentFactories.AddStub<PortRenderer>();
        var source = new FlowDesignerNode("route", "router", "Choose agent", new FlowNodePosition(10, 20), "3 routes");
        var node = new FlowDiagramNode(source);

        var rendered = context.Render<FlowNodeWidget>(parameters => parameters.Add(component => component.Node, node));

        Assert.IsNotNull(rendered.Find(".flow-node.router"));
        StringAssert.Contains(rendered.Markup, "Choose agent");
        StringAssert.Contains(rendered.Markup, "3 routes");
    }
}
