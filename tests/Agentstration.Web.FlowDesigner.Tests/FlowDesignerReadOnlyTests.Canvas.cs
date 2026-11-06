using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Components.State;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using AngleSharp.Dom;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Geometry;
using BlazorMonaco.Editor;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void CanvasLeavesTheMouseWheelToPageScrollingAndSupportsExplicitAndCtrlWheelZoom()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowCanvas>(parameters => parameters
            .Add(component => component.Document, new FlowDesignerDocument([], [])));
        var diagram = GetDiagram(rendered.Instance);

        Assert.IsFalse(diagram.Options.Zoom.Enabled);
        rendered.FindAll(".flow-zoom-controls button").Single(button => button.TextContent.Trim() == "+").Click();
        Assert.AreEqual(1.15, diagram.Zoom, 0.001);
        rendered.Instance.ApplyCtrlWheelZoom(-120);
        Assert.AreEqual(1.30, diagram.Zoom, 0.001);
    }

    [TestMethod]
    public void CanvasHidesErrorTransitionsUntilTheirSourceOrTransitionIsSelectedOrAllAreRequested()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var document = new FlowDesignerDocument(
            [
                new("agent", "agent", "Agent", new(20, 20), "sample-agent", ["success", "error"]),
                new("output", "output", "Output", new(300, 20), null, []),
                new("failure", "output", "Error", new(300, 220), null, [])
            ],
            [
                new("agent-success-output", "agent", "output", "success"),
                new("agent-error-failure", "agent", "failure", "error")
            ]);
        var rendered = context.Render<FlowCanvas>(parameters => parameters
            .Add(component => component.Document, document)
            .Add(component => component.Revision, 1));

        CollectionAssert.AreEquivalent(
            new[] { "agent-success-output" },
            GetDiagram(rendered.Instance).Links.Select(link => link.Id).ToArray());

        rendered.Render(parameters => parameters
            .Add(component => component.Document, document)
            .Add(component => component.Revision, 1)
            .Add(component => component.SelectedStepName, "agent"));
        Assert.AreEqual(2, GetDiagram(rendered.Instance).Links.Count);

        rendered.Render(parameters => parameters
            .Add(component => component.Document, document)
            .Add(component => component.Revision, 1)
            .Add(component => component.SelectedStepName, (string?)null)
            .Add(component => component.SelectedTransitionId, "agent-error-failure"));
        Assert.IsTrue(GetDiagram(rendered.Instance).Links.Any(link => link.Id == "agent-error-failure" && link.Selected));

        rendered.Render(parameters => parameters
            .Add(component => component.Document, document)
            .Add(component => component.Revision, 1)
            .Add(component => component.SelectedTransitionId, (string?)null)
            .Add(component => component.ShowAllErrorTransitions, true));
        Assert.AreEqual(2, GetDiagram(rendered.Instance).Links.Count);
    }

    [TestMethod]
    public async Task DeletingCanvasNodeUpdatesDraftAndDoesNotReappearAfterMovingAnotherNode()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "agent", Agent = new("welcome-agent") },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions =
            [
                new("input-agent", "input", "completed", "agent"),
                new("agent-output", "agent", "success", "output")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var canvas = rendered.FindComponent<FlowCanvas>();
        var diagram = GetDiagram(canvas.Instance);
        var input = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "input");
        var agent = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "agent");
        var initialRevision = store.State.LocalRevision;

        Assert.IsFalse(await diagram.Options.Constraints.ShouldDeleteNode!(input));
        Assert.IsTrue(await diagram.Options.Constraints.ShouldDeleteNode!(agent));
        await rendered.InvokeAsync(() => diagram.SelectModel(input, unselectOthers: true));
        await rendered.InvokeAsync(() => KeyboardShortcutsDefaults.DeleteSelection(diagram).AsTask());
        Assert.IsTrue(diagram.Nodes.OfType<FlowDiagramNode>().Any(node => node.Source.Name == "input"));
        await rendered.InvokeAsync(() => diagram.SelectModel(agent, unselectOthers: true));
        await rendered.InvokeAsync(() => KeyboardShortcutsDefaults.DeleteSelection(diagram).AsTask());
        rendered.WaitForAssertion(() => Assert.IsFalse(store.State.Resource!.Definition.Steps.Any(step => step.Name == "agent")));
        Assert.IsEmpty(store.State.Resource!.Definition.Transitions);
        Assert.IsNull(store.State.Selection.StepName);
        Assert.AreEqual(initialRevision + 1, store.State.LocalRevision);
        Assert.AreEqual(FlowSaveState.UnsavedChanges, store.State.SaveState);

        canvas = rendered.FindComponent<FlowCanvas>();
        await rendered.InvokeAsync(() => canvas.Instance.StepMoved.InvokeAsync(new MoveStepCommand("output", new(300, 200))));
        Assert.IsFalse(store.State.Resource!.Definition.Steps.Any(step => step.Name == "agent"));
        Assert.IsFalse(store.State.Diagram.Nodes.Any(node => node.Name == "agent"));
        Assert.IsFalse(GetDiagram(canvas.Instance).Nodes.OfType<FlowDiagramNode>().Any(node => node.Source.Name == "agent"));
        Assert.AreEqual(initialRevision + 2, store.State.LocalRevision);
    }

    [TestMethod]
    public async Task SelectingCanvasTransitionShowsEditableMetadata()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("input-output", "input", "completed", "output")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var diagram = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance);

        await rendered.InvokeAsync(() => diagram.SelectModel(diagram.Links.Single(), unselectOthers: true));
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='selected-transition-editor']")));
        await rendered.Find("[data-testid='transition-event']").ChangeAsync(new() { Value = "approved" });
        await rendered.Find("[data-testid='transition-condition']").ChangeAsync(new() { Value = "${input.approved}" });
        await rendered.Find("[data-testid='transition-priority']").ChangeAsync(new() { Value = "3" });

        var transition = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Transitions.Single();
        Assert.AreEqual("input-output", transition.Id);
        Assert.AreEqual("approved", transition.Event);
        Assert.AreEqual("${input.approved}", transition.Condition);
        Assert.AreEqual(3, transition.Priority);
    }

    [TestMethod]
    public async Task SelectingInspectorTransitionSelectsCanvasLink()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("input-output", "input", "completed", "output")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var diagram = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance);

        await rendered.Find(".transition-list li > button:first-child").ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.IsTrue(diagram.Links.Single().Selected));
    }

    [TestMethod]
    public async Task ReconnectingCanvasTransitionUpdatesItsTargetAndPreservesMetadata()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition { Name = "transform" },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions = [new("route", "input", "completed", "transform", "${input.ready}", 4)]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var diagram = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance);
        var output = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "output");

        await rendered.InvokeAsync(() => diagram.Links.Single().SetTarget(new SinglePortAnchor(output.Input)));

        var transition = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Transitions.Single();
        Assert.AreEqual("input", transition.FromStep);
        Assert.AreEqual("output", transition.ToStep);
        Assert.AreEqual("completed", transition.Event);
        Assert.AreEqual("${input.ready}", transition.Condition);
        Assert.AreEqual(4, transition.Priority);
    }
}
