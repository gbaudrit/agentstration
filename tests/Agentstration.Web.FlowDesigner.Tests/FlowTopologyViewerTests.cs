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
        Assert.IsTrue(rendered.Find("svg").ClassList.Contains("fit"));
        Assert.Contains("width:100%", rendered.Find("svg").GetAttribute("style")!, StringComparison.Ordinal);
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
        Assert.IsTrue(rendered.FindAll("marker").All(marker => marker.GetAttribute("markerUnits") == "userSpaceOnUse"));
        Assert.AreEqual("Conditional workflow", rendered.Find("svg").GetAttribute("aria-label"));

        rendered.FindAll("[role=button]")[1].Click();

        Assert.AreEqual("condition", selected);
    }

    [TestMethod]
    public void ObservedHandoffsRemainVisibleOnGraphWithoutAPathOverlay()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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
            .Add(component => component.Graph, graph));

        Assert.IsEmpty(rendered.FindAll(".topology-path-panel"));
        Assert.IsEmpty(rendered.FindAll("button.path-toggle"));
        Assert.HasCount(1, rendered.FindAll(".topology-edge.observed"));
        Assert.Contains("#1", rendered.Markup, StringComparison.Ordinal);
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

    [TestMethod]
    public void ViewerRoutesOppositeHandoffsInParallelAndUsesIntrinsicCanvasWidth()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var graph = new FlowTopologyGraph(
            [
                new("participant:welcome", "welcome", "Welcome", "agent", 0, 0),
                new("participant:advisor", "advisor", "Advisor", "agent", 300, 0),
                new("system:output", "output", "Output", "output", 600, 0)
            ],
            [
                new("forward", "participant:welcome", "participant:advisor", "handoff · #1", FlowTopologyEdgeKind.Conditional),
                new("backward", "participant:advisor", "participant:welcome", "handoff · #2", FlowTopologyEdgeKind.Conditional),
                new("terminal:welcome", "participant:welcome", "system:output", "terminal", FlowTopologyEdgeKind.Dynamic),
                new("terminal:advisor", "participant:advisor", "system:output", "terminal", FlowTopologyEdgeKind.Dynamic)
            ],
            "directed",
            "Handoff orchestration");

        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph));

        var forward = rendered.Find("path[data-edge-id='forward']").GetAttribute("d");
        var backward = rendered.Find("path[data-edge-id='backward']").GetAttribute("d");
        Assert.AreNotEqual(forward, backward);
        Assert.Contains(" C ", forward!, StringComparison.Ordinal);
        Assert.Contains(" C ", backward!, StringComparison.Ordinal);
        Assert.DoesNotContain(" Q ", forward!, StringComparison.Ordinal);
        Assert.DoesNotContain(" Q ", backward!, StringComparison.Ordinal);
        Assert.HasCount(2, rendered.FindAll(".edge-label-wrap"));
        Assert.IsFalse(rendered.Find("svg").ClassList.Contains("fit"));
        Assert.Contains("px", rendered.Find("svg").GetAttribute("style")!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ViewerRoutesNonAdjacentVerticalHandoffsThroughSeparateSideLanes()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var graph = new FlowTopologyGraph(
            [
                new("participant:top", "top", "Top", "agent", 300, 0),
                new("participant:middle", "middle", "Middle", "agent", 300, 170),
                new("participant:bottom", "bottom", "Bottom", "agent", 300, 340)
            ],
            [
                new("top-bottom", "participant:top", "participant:bottom", Kind: FlowTopologyEdgeKind.Conditional),
                new("bottom-top", "participant:bottom", "participant:top", Kind: FlowTopologyEdgeKind.Conditional)
            ],
            "directed",
            "Vertical handoffs");

        var rendered = context.Render<FlowTopologyViewer>(parameters => parameters
            .Add(component => component.Graph, graph));

        var downward = rendered.Find("path[data-edge-id='top-bottom']").GetAttribute("d");
        var upward = rendered.Find("path[data-edge-id='bottom-top']").GetAttribute("d");
        Assert.AreNotEqual(downward, upward);
        Assert.Contains("C 312", downward!, StringComparison.Ordinal);
        Assert.Contains("C 296", upward!, StringComparison.Ordinal);
    }
}
