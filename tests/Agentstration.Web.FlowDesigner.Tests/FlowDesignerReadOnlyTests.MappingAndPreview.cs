using System.Text.Json;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Models;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void MappingEditorAcceptsSuggestedExpressionsFreeTextAndJsonWithoutChangingTheContract()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        JsonElement? value = JsonSerializer.SerializeToElement("${transition.output}");
        var rendered = context.Render<FlowDataMappingEditor>(parameters => parameters.Add(component => component.Value, value)
            .Add(component => component.Options, [new("${input.content}", "Artifact content")])
            .Add(component => component.ValueChanged, next => value = next));
        rendered.Find(".mapping-picker-toggle").Click();
        Assert.Contains("${input.content}", rendered.Find("[role=option]").TextContent);
        rendered.Find("input").Change("${input.content}");
        Assert.AreEqual("${input.content}", value!.Value.GetString());
        rendered.Find("input").Change("Free literal text");
        Assert.AreEqual("Free literal text", value!.Value.GetString());
        rendered.Find("button").Click();
        rendered.Find("textarea").Change("{\"content\":\"${input.content}\"}");
        Assert.AreEqual("${input.content}", value!.Value.GetProperty("content").GetString());
        var saved = value.Value.GetRawText();
        rendered.Find("textarea").Change("{");
        Assert.HasCount(1, rendered.FindAll("[role=alert]"));
        Assert.AreEqual(saved, value!.Value.GetRawText());
        rendered.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        Assert.IsTrue(rendered.Find("textarea").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task IncompatibleDragIsRedAndExplainedThenRestoresWithoutMutatingTransitions()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new(0, 0, 1024, 768));
        var document = new FlowDesignerDocument([new("producer", "tool", "Clock", new(0, 0), null, ["success"]), new("target", "agent", "Redactor", new(300, 0), null, ["success"])], []);
        var creates = 0;
        var rendered = context.Render<FlowCanvas>(parameters => parameters.Add(component => component.Document, document)
            .Add(component => component.ConnectionProblem, (_, _, _, _) => "Expected an object with text")
            .Add(component => component.TransitionCreated, _ => creates++));
        var diagram = GetDiagram(rendered.Instance);
        var source = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "producer").Outputs[0];
        var target = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "target").Input;
        var behavior = diagram.GetBehavior<DragNewLinkBehavior>()!;
        var args = new Blazor.Diagrams.Core.Events.PointerEventArgs(0, 0, 0, 0, false, false, false, 1, 0, 0, 0, 0, 0, "mouse", true);
        await rendered.InvokeAsync(() => behavior.StartFrom(source, 0, 0));
        await rendered.InvokeAsync(() => { diagram.SetZoom(1); diagram.SetPan(0, 0); });
        target.Position = new(300, 0); target.Size = new(14, 14); target.Initialized = true;
        var overTarget = args with { ClientX = 307, ClientY = 7 };
        await rendered.InvokeAsync(() => diagram.TriggerPointerMove(null, overTarget));
        Assert.AreEqual("var(--color-danger)", ((LinkModel)behavior.OngoingLink!).Color);
        Assert.AreEqual("Expected an object with text", target.ContractProblem);
        Assert.Contains("Expected an object with text", rendered.Find("[role=status]").TextContent);
        Assert.AreEqual(0, creates);
        await rendered.InvokeAsync(() => diagram.TriggerPointerMove(null, args with { ClientX = 200, ClientY = 100 }));
        Assert.IsNull(target.ContractProblem);
        Assert.AreNotEqual("var(--color-danger)", ((LinkModel)behavior.OngoingLink!).Color);
        Assert.HasCount(0, rendered.FindAll("[role=status]"));
        await rendered.InvokeAsync(() => diagram.TriggerPointerUp(null, args));
    }

    [TestMethod]
    public void OpeningAPublishedFlowStillMarksItsIncompatibleTransitionWithoutChangingIt()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new(0, 0, 1024, 768));
        var document = new FlowDesignerDocument([new("producer", "tool", "Clock", new(0, 0), null, ["success"]), new("target", "agent", "Redactor", new(300, 0), null, ["success"])], [new("route", "producer", "target", "success")]);
        var rendered = context.Render<FlowCanvas>(parameters => parameters.Add(component => component.Document, document).Add(component => component.IsReadOnly, true)
            .Add(component => component.ConnectionProblem, (_, _, _, _) => "Incompatible"));
        var diagram = GetDiagram(rendered.Instance);
        Assert.AreEqual("var(--color-danger)", ((LinkModel)diagram.Links.Single()).Color);
        Assert.IsTrue(diagram.Links.Single().Locked);
        Assert.AreEqual("route", document.Links.Single().Id);
    }
    [TestMethod]
    public void OutputMappingCanBeResetToTheIncomingTransitionWithoutChangingTheContract()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var output = new Agentstration.Flows.OutputFlowStepDefinition { Name = "completed", Schema = schema,
            OutputMapping = JsonSerializer.SerializeToElement("${steps.transform.output}") };
        Agentstration.Flows.OutputFlowStepDefinition? changed = null;
        var rendered = context.Render<FlowOutputInspector>(parameters => parameters.Add(component => component.Step, output)
            .Add(component => component.ActiveTab, FlowStepSettingsTab.Output)
            .Add(component => component.UpdateSelectedAsync, value => changed = (Agentstration.Flows.OutputFlowStepDefinition)value));
        Assert.Contains("Explicit output mapping", rendered.Markup);
        Assert.HasCount(1, rendered.FindAll("[data-testid=output-value-mapping]"));
        rendered.Find("[data-testid=output-use-transition]").Click();
        Assert.AreEqual("${transition.output}", changed!.OutputMapping!.Value.GetString());
        Assert.IsTrue(JsonElement.DeepEquals(schema, changed.Schema!.Value));
        rendered.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        Assert.HasCount(0, rendered.FindAll("[data-testid=output-value-mapping]"));
        Assert.Contains("read-only", rendered.Markup);
    }
}
