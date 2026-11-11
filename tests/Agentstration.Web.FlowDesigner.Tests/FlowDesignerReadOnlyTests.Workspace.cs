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
    public async Task ReadOnlyModeDisablesMutatingActions()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var backend = new BackendStub();
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var @namespace = new ResourceNamespace("pack.sample");
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, @namespace));

        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Trim() is "Save" or "Publish" or "Run draft"));
        Assert.AreEqual(0, backend.SaveCount);
        StringAssert.Contains(rendered.Markup, "Read only");
        StringAssert.Contains(rendered.Markup, "pack.sample");
        StringAssert.Contains(rendered.Markup, "Published version 2.1.0");
        Assert.AreEqual(@namespace, backend.LoadedTarget?.Namespace);
        Assert.AreEqual("Steps", rendered.Find("#palette-steps-heading").TextContent);
        Assert.AreEqual("View", rendered.Find("#palette-view-heading").TextContent);
        Assert.HasCount(10, rendered.FindAll(".step-palette-group:not(.palette-view) button .ui-icon"));
        Assert.HasCount(4, rendered.FindAll(".palette-view button .ui-icon"));

        var readOnlyCanvas = rendered.FindComponent<FlowCanvas>();
        var readOnlyDiagram = GetDiagram(readOnlyCanvas.Instance);
        var entryNode = readOnlyDiagram.Nodes.OfType<FlowDiagramNode>().Single();
        Assert.IsFalse(await readOnlyDiagram.Options.Constraints.ShouldDeleteNode!(entryNode));

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Definition").Click();
        var sourceEditor = rendered.FindComponent<FlowSourceEditor>();
        Assert.AreEqual("entryStep: input", sourceEditor.Instance.Value);
        Assert.IsNull(sourceEditor.Instance.Error);
        Assert.IsTrue(sourceEditor.Instance.IsReadOnly);
        var applyYaml = rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Apply and save");
        Assert.IsTrue(applyYaml.HasAttribute("disabled"));
        Assert.HasCount(0, rendered.FindAll(".source-editor .muted"));
    }

    [TestMethod]
    public async Task DesignerWorkspaceControlsCollapsePanelsValidationAndEnterFocusMode()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var backend = new BackendStub(readOnly: false);
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));

        Assert.HasCount(0, rendered.FindAll(".validation-dock.expanded"));
        Assert.HasCount(0, rendered.FindAll(".validation-dock .muted"));
        rendered.Find(".validation-summary").Click();
        Assert.HasCount(1, rendered.FindAll(".validation-dock.expanded"));
        Assert.HasCount(1, rendered.FindAll(".validation-dock .muted"));
        rendered.Find(".validation-summary").Click();
        Assert.HasCount(0, rendered.FindAll(".validation-dock.expanded"));
        await rendered.Find("[data-testid='flow-designer-validation-run']").ClickAsync(new());
        Assert.AreEqual(1, backend.ValidationCount);
        Assert.HasCount(1, rendered.FindAll(".validation-dock.expanded"));

        rendered.Find("button[aria-label='Hide step palette']").Click();
        StringAssert.Contains(rendered.Find(".flow-editor-shell").ClassName, "palette-closed");
        rendered.Find("button[aria-label='Hide inspector']").Click();
        StringAssert.Contains(rendered.Find(".flow-editor-shell").ClassName, "inspector-closed");

        rendered.Find("button[aria-label='Enter full screen mode']").Click();
        StringAssert.Contains(rendered.Find(".designer-toolbar").ClassName, "designer-focus");
        StringAssert.Contains(rendered.Find(".flow-editor-shell").ClassName, "focus-mode");
        StringAssert.Contains(rendered.Find(".flow-editor-shell").ClassName, "palette-closed");
        StringAssert.Contains(rendered.Find(".flow-editor-shell").ClassName, "inspector-closed");
        Assert.HasCount(1, rendered.FindAll(".designer-focus-toolbar button[aria-label='Exit']"));
        Assert.HasCount(1, rendered.FindAll(".validation-dock"));

        rendered.Find(".desktop-panel-toggle[aria-label='Show step palette']").Click();
        Assert.IsFalse((rendered.Find(".flow-editor-shell").ClassName ?? string.Empty).Contains("palette-closed", StringComparison.Ordinal));
        rendered.FindAll(".designer-focus-toolbar button").Single(button => button.TextContent.Trim() == "Definition").Click();
        Assert.HasCount(1, rendered.FindAll(".flow-editor-shell.definition .source-editor"));
        Assert.HasCount(1, rendered.FindAll(".designer-focus-toolbar button[aria-label='Exit']"));

        await rendered.InvokeAsync(rendered.Instance.ExitFullScreenAsync);
        Assert.HasCount(0, rendered.FindAll(".designer-focus-toolbar"));
    }

    [TestMethod]
    public void DraftRunUsesTheDraftInputSchemaEditorAndBlocksInvalidInput()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { prompt = new { type = "string" } },
            required = new[] { "prompt" }
        });
        var definition = new FlowGraphDefinition { EntryStep = "input", InputSchema = schema, Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "sample"));
        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Run draft").Click();

        var editor = rendered.FindComponent<JsonSchemaInputEditor>();
        Assert.AreEqual(schema.GetRawText(), editor.Instance.Schema?.GetRawText());
        Assert.HasCount(1, rendered.FindAll("[data-testid='flow-designer-run-dialog']"));
        Assert.IsTrue(rendered.Find("[data-testid='flow-designer-run-submit']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ReadOnlyModeUsesTheSelectedFrenchCulture()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub());
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, new ResourceNamespace("pack.sample")));

        StringAssert.Contains(rendered.Markup, "Lecture seule");
        StringAssert.Contains(rendered.Markup, "Version publiée 2.1.0");
        StringAssert.Contains(rendered.Markup, "Ajuster");

        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowDesignerStrings>>();
        Assert.AreEqual("Appliquer et enregistrer", strings["ApplyAndSaveYaml"].Value);
        Assert.AreEqual("2 agents disponibles. Les routes sont explicites et restent des références immuables après publication.", strings["AvailableAgents.Many", 2].Value);
    }
}
