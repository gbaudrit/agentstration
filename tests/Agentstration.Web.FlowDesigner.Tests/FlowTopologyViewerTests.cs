using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowTopologyViewerTests
{
    [TestMethod]
    public void CanvasExposesZoomControlsInReadOnlyMode()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowDesignerStrings>>();
        var graph = new FlowTopologyGraph(
            [new("step:input", "input", "Input", "input", 0, 0)],
            [],
            "graph",
            "Read-only topology");
        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph));

        Assert.AreEqual("100%", rendered.Find(".topology-zoom-controls span").TextContent.Trim());
        rendered.Find($"button[aria-label='{strings["ZoomIn"].Value}']").Click();
        Assert.AreEqual("115%", rendered.Find(".topology-zoom-controls span").TextContent.Trim());
        Assert.IsNotNull(rendered.Find($"button[aria-label='{strings["ZoomOut"].Value}']"));
        rendered.FindAll(".topology-zoom-controls button").Single(button => button.TextContent.Trim() == strings["Fit"].Value).Click();
        Assert.AreEqual("100%", rendered.Find(".topology-zoom-controls span").TextContent.Trim());
    }

    [TestMethod]
    public void ViewerRendersDirectedEdgesAndSupportsNodeSelection()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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

    [TestMethod]
    public void ObservedPathUsesVerticalPanelAndHighlightsSelectedHandoff()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var selected = string.Empty;
        var graph = new FlowTopologyGraph(
            [
                new("participant:welcome", "welcome", "Welcome", "agent", 0, 0),
                new("participant:advisor", "advisor", "Advisor", "agent", 220, 0)
            ],
            [new("handoff:welcome:advisor", "participant:welcome", "participant:advisor", "handoff · #1", State: FlowTopologyEdgeState.Observed)],
            "orchestration",
            "Handoff orchestration")
        {
            Transfers = [new(1, "welcome", "advisor", 7)]
        };

        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph)
            .Add(component => component.SelectedKeyChanged, value => selected = value));

        Assert.IsEmpty(rendered.FindAll(".topology-path-panel"));
        Assert.IsEmpty(rendered.FindAll(".topology-observed-path"));

        rendered.Find("button.path-toggle").Click();

        Assert.HasCount(1, rendered.FindAll(".topology-path-panel ol li"));
        rendered.Find(".topology-path-panel ol li button").Click();
        Assert.AreEqual("advisor", selected);
        Assert.HasCount(1, rendered.FindAll(".topology-edge.path-selected"));
    }

    [TestMethod]
    public void ViewerSupportsExpandedModeAndInspectorToggle()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        bool? inspectorVisible = null;
        var graph = new FlowTopologyGraph(
            [new("step:input", "input", "Input", "input", 0, 0)],
            [],
            "graph",
            "Read-only topology");
        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph)
            .Add(component => component.ShowInspectorControl, true)
            .Add(component => component.InspectorVisible, true)
            .Add(component => component.InspectorVisibleChanged, value => inspectorVisible = value));

        rendered.Find("button.expand-toggle").Click();
        Assert.IsTrue(rendered.Find(".topology-shell").ClassList.Contains("expanded"));

        rendered.Find("button.inspector-toggle").Click();
        Assert.AreEqual(false, inspectorVisible);
    }
}
