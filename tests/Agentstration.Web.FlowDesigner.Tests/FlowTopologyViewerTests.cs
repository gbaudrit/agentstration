using Agentstration.Web.FlowDesigner.Components;
using Bunit;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowTopologyViewerTests
{
    [TestMethod]
    public void CanvasExposesZoomControlsInReadOnlyMode()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true)
            .SetResult(new(0, 0, 800, 600));

        var rendered = context.Render<FlowCanvas>(parameters => parameters
            .Add(component => component.Document, new([], []))
            .Add(component => component.IsReadOnly, true));

        Assert.AreEqual("100%", rendered.Find(".flow-zoom-controls span").TextContent.Trim());
        rendered.Find("button[aria-label='Zoom in']").Click();
        Assert.AreEqual("115%", rendered.Find(".flow-zoom-controls span").TextContent.Trim());
        Assert.IsNotNull(rendered.Find("button[aria-label='Zoom out']"));
        Assert.IsNotNull(rendered.FindAll(".flow-zoom-controls button").Single(button => button.TextContent.Trim() == "Fit"));
    }

    [TestMethod]
    public void ViewerRendersDirectedEdgesAndSupportsNodeSelection()
    {
        using var context = new BunitContext();
        var selected = string.Empty;
        var graph = new FlowTopologyGraph(
            [
                new("step:input", "input", "Input", "input", 0, 0),
                new("step:condition", "condition", "Decision", "condition", 220, 0)
            ],
            [new("next", "step:input", "step:condition", "completed", FlowTopologyEdgeKind.Conditional)],
            "graph",
            "Conditional workflow");

        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph)
            .Add(component => component.SelectedKeyChanged, value => selected = value));

        Assert.HasCount(2, rendered.FindAll("[role=button]"));
        Assert.HasCount(1, rendered.FindAll(".topology-edge.conditional"));
        Assert.AreEqual("Conditional workflow", rendered.Find("svg").GetAttribute("aria-label"));

        rendered.FindAll("[role=button]")[1].Click();

        Assert.AreEqual("condition", selected);
    }
}
