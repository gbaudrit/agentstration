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

[TestClass]
[DoNotParallelize]
public sealed class FlowDesignerReadOnlyTests
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
                new("failure", "failure", "Failure", new(300, 220), null, [])
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
        Assert.HasCount(8, rendered.FindAll(".step-palette-group:not(.palette-view) button .ui-icon"));
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
    public async Task DefinitionAndSplitBindDraftSourceAndActualApplyDiagnostic()
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
        var store = context.Services.GetRequiredService<FlowEditorStore>();

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Definition").Click();
        var editor = rendered.FindComponent<FlowSourceEditor>();
        Assert.AreEqual("entryStep: input", editor.Instance.Value);
        Assert.IsNull(editor.Instance.Error);
        Assert.IsFalse(editor.Instance.IsReadOnly);
        StringAssert.Contains(rendered.Find(".source-editor h2").TextContent, "Flow definition (YAML)");
        StringAssert.Contains(rendered.Find(".source-editor .muted").TextContent, "save the draft");
        Assert.HasCount(0, rendered.FindAll(".error-panel"));
        Assert.HasCount(1, rendered.FindAll(".flow-editor-shell.definition .source-editor"));
        Assert.HasCount(0, rendered.FindAll(".flow-editor-shell.definition .flow-canvas-wrap"));

        var validSource = "entryStep: input\nsteps:\n  - name: input\n    type: input";
        backend.ReplacementDefinition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "result" }],
            Transitions = [new("input-result", "input", "completed", "result")]
        };
        await rendered.InvokeAsync(() => editor.Instance.ApplyRequested.InvokeAsync(validSource));
        rendered.WaitForAssertion(() => Assert.HasCount(2, store.State.Diagram.Nodes));
        Assert.AreEqual(validSource, rendered.FindComponent<FlowSourceEditor>().Instance.Value);
        Assert.IsNull(store.State.SourceError);
        Assert.AreEqual(FlowSaveState.Saved, store.State.SaveState);
        Assert.AreEqual(validSource, backend.LastReplacementSource);

        var invalidSource = "entryStep: [invalid";
        backend.ReplacementError = "YAML parse error at line 1: expected ']'.";
        editor = rendered.FindComponent<FlowSourceEditor>();
        await rendered.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(invalidSource));
        await rendered.InvokeAsync(() => editor.Instance.ApplyRequested.InvokeAsync(invalidSource));
        rendered.WaitForAssertion(() => Assert.AreEqual(backend.ReplacementError, rendered.FindComponent<FlowSourceEditor>().Instance.Error));
        Assert.AreEqual(invalidSource, store.State.SourceText);
        Assert.AreEqual(FlowSaveState.SaveFailed, store.State.SaveState);
        Assert.AreEqual(invalidSource, rendered.FindComponent<FlowSourceEditor>().Instance.Value);
        Assert.HasCount(2, store.State.Diagram.Nodes);
        StringAssert.Contains(rendered.Find(".source-editor .error-panel").TextContent, "line 1");
        Assert.AreEqual(invalidSource, backend.LastReplacementSource);

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Split").Click();
        Assert.HasCount(1, rendered.FindAll(".flow-editor-shell.split .flow-canvas-wrap"));
        Assert.AreEqual(invalidSource, rendered.FindComponent<FlowSourceEditor>().Instance.Value);
        Assert.AreEqual(backend.ReplacementError, rendered.FindComponent<FlowSourceEditor>().Instance.Error);
    }

    [TestMethod]
    public async Task TypingInDefinitionDoesNotReplaceMonacoContent()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult("entryStep: inputa");
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Definition").Click();

        var setValueCalls = context.JSInterop.Invocations.Count(call => call.Identifier == "blazorMonaco.editor.setValue");
        var monaco = rendered.FindComponent<StandaloneCodeEditor>();
        await rendered.InvokeAsync(() => monaco.Instance.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));

        Assert.AreEqual("entryStep: inputa", context.Services.GetRequiredService<FlowEditorStore>().State.SourceText);
        Assert.AreEqual(setValueCalls, context.JSInterop.Invocations.Count(call => call.Identifier == "blazorMonaco.editor.setValue"));
    }

    [TestMethod]
    public async Task ApplyingYamlShowsProgressAndRestoresTheButton()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue").SetResult("entryStep: input");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = context.Render<FlowSourceEditor>(parameters => parameters
            .Add(component => component.Value, "entryStep: input")
            .Add(component => component.ApplyRequested,
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, async _ => { await completion.Task; })));

        var applying = rendered.Find(".source-editor button").ClickAsync(new());
        rendered.WaitForAssertion(() =>
        {
            var button = rendered.Find(".source-editor button");
            Assert.IsTrue(button.HasAttribute("disabled"));
            Assert.AreEqual("true", button.GetAttribute("aria-busy"));
            StringAssert.Contains(button.TextContent, "Applying and saving");
            Assert.HasCount(1, rendered.FindAll(".source-apply-spinner"));
        });

        completion.SetResult(true);
        await applying;
        rendered.WaitForAssertion(() =>
        {
            var button = rendered.Find(".source-editor button");
            Assert.IsFalse(button.HasAttribute("disabled"));
            Assert.AreEqual("false", button.GetAttribute("aria-busy"));
            StringAssert.Contains(button.TextContent, "Apply and save");
        });
    }

    [TestMethod]
    public async Task DefinitionEditorFollowsDarkThemeChanges()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var preferences = context.Services.GetRequiredService<UserPreferencesState>();
        await preferences.SetThemeAsync(UserTheme.Dark, CancellationToken.None);
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Definition").Click();
        var monaco = rendered.FindComponent<StandaloneCodeEditor>().Instance;
        Assert.AreEqual("vs-dark", monaco.ConstructionOptions!(monaco).Theme);

        await rendered.InvokeAsync(() => preferences.SetThemeAsync(UserTheme.Light, CancellationToken.None));
        rendered.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(call =>
            call.Identifier == "blazorMonaco.editor.setTheme" && call.Arguments[0]?.ToString() == "vs")));
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

    [TestMethod]
    public void PublishVersionStartsAfterTheLatestPublishedVersionAndSurvivesSave()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var backend = new BackendStub(readOnly: false);
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub("1.0.9", "1.0.10", "1.0.0"));
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var version = rendered.Find("input[aria-label='Publish version']");
        Assert.AreEqual("1.0.11", version.GetAttribute("value"));

        context.Services.GetRequiredService<FlowEditorStore>().SetSource("entryStep: input # edited");
        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();
        Assert.AreEqual("1.0.11", rendered.Find("input[aria-label='Publish version']").GetAttribute("value"));

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Publish").Click();
        Assert.AreEqual("1.0.11", backend.LastPublishVersion);
        Assert.AreEqual("1.0.12", rendered.Find("input[aria-label='Publish version']").GetAttribute("value"));
    }

    [TestMethod]
    public void DuplicatePublishVersionShowsLocalizedFeedbackInsteadOfStorageError()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext();
        var backend = new BackendStub(readOnly: false) { VersionConflict = true };
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub("1.0.0"));
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.Find("input[aria-label='Version à publier']").Change("1.0.0");
        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Publier").Click();

        Assert.AreEqual("1.0.0", backend.LastPublishVersion);
        var feedback = rendered.Find(".designer-feedback[role='alert']");
        StringAssert.Contains(feedback.TextContent, "La version 1.0.0 est déjà publiée");
        Assert.IsFalse(feedback.TextContent.Contains("SQLite", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Publier").HasAttribute("disabled"));
    }

    [TestMethod]
    public void EditablePaletteUsesOneGenericFlowCardAndShowsTheSelectedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
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
            .Add(component => component.ResourceId, "parent"));
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Flow", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        var flowSummaryLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(flowSummaryLink.TextContent, "News analysis");
        Assert.AreEqual("/namespaces/pack.news/flows/analysis", flowSummaryLink.GetAttribute("href"));
        Assert.AreEqual("_blank", flowSummaryLink.GetAttribute("target"));
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Flow");
            StringAssert.Contains(rendered.Markup, "summary");
        });
        Assert.AreEqual("/namespaces/pack.news/flows/analysis", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Flow ID");

        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.String, call.InputMapping?.ValueKind);
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.IsTrue(rendered.Find("[data-testid='flow-pass-transition-output']").HasAttribute("checked"));
        StringAssert.Contains(rendered.Markup, "Pass incoming transition output");
        var errorTransition = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Transitions
            .Single(transition => transition.FromStep == call.Name && transition.ToStep == "error");
        Assert.AreEqual("rejected", errorTransition.Event);

        rendered.Find("[data-testid='flow-pass-transition-output']").Change(false);
        call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.Object, call.InputMapping?.ValueKind);
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "article"));
    }

    [TestMethod]
    public void NewAgentAndOutputStepsMapTheIncomingTransitionOutput()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Output").Click();

        var steps = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps;
        Assert.AreEqual("${transition.output}", steps.OfType<AgentFlowStepDefinition>().Single().InputMapping?.GetString());
        Assert.AreEqual("${transition.output}", steps.OfType<OutputFlowStepDefinition>().Last().OutputMapping?.GetString());
    }

    [TestMethod]
    public void AddingAgentStepConnectsItsErrorOutputToTheFirstErrorTerminal()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions = [new("input-completed", "input", "completed", "completed")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();

        var updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        var agent = updated.Steps.OfType<AgentFlowStepDefinition>().Single();
        var transition = updated.Transitions.Single(item => item.Event == "error");
        Assert.AreEqual(agent.Name, transition.FromStep);
        Assert.AreEqual("error", transition.ToStep);
        Assert.AreEqual($"{agent.Name}-error-error", transition.Id);
        Assert.IsFalse(rendered.FindAll(".step-palette button").Any(button => button.TextContent.Trim() == "Failure"));
    }

    [TestMethod]
    public void AddingAndSelectingAgentUsesTechnicalReferenceAndResourceDisplayName()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new("welcome-agent", "Welcome Agent"),
                new("packed-agent", "Pack Agent") { Namespace = new("pack.demo") }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim() == "Agent")));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.Find("[data-testid='configure-selected-step']").Click();

        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => Assert.AreEqual("Welcome Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        var step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("welcome-agent", step.Agent.ResourceId);
        StringAssert.Contains(rendered.Find(".inspector-details-group").TextContent, "Step ID");
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        Assert.AreEqual("/agents/welcome-agent", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Agent ID");

        rendered.Find(".flow-inspector select").Change("pack.demo|packed-agent");
        rendered.WaitForAssertion(() => Assert.AreEqual("Pack Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("packed-agent", step.Agent.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.demo"), step.Agent.Namespace);
        Assert.AreEqual("/namespaces/pack.demo/agents/packed-agent", rendered.Find(".inspector-resource-link").GetAttribute("href"));
    }

    [TestMethod]
    public async Task InspectorAlwaysShowsDisplayAndTechnicalResourceNames()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("same-flow", FlowCallVersionStrategy.Exact, "1.0.0")
                },
                new AgentFlowStepDefinition
                {
                    Name = "agent",
                    Agent = new("same-agent")
                }
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents = [new("same-agent", "same-agent")],
            Flows = [new("same-flow", "same-flow", ResourceNamespace.Default, "1.0.0")]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        var canvas = rendered.FindComponent<FlowCanvas>();

        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("child"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("same-flow", rendered.Find(".inspector-resource-summary-link strong").TextContent);
            Assert.AreEqual("same-flow", rendered.Find(".inspector-step-summary-copy code").TextContent);
        });

        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("agent"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("same-agent", rendered.Find(".inspector-resource-summary-link strong").TextContent);
            Assert.AreEqual("same-agent", rendered.Find(".inspector-step-summary-copy code").TextContent);
        });
    }

    [TestMethod]
    public async Task SelectingLegacyGenericAgentLabelUsesCurrentResourceName()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "agent", DisplayName = "Agent", Agent = new("welcome-agent") }
            ],
            Transitions = [new("input-agent", "input", "completed", "agent")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents = [new("welcome-agent", "Welcome Agent")]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.WaitForAssertion(() => Assert.HasCount(2, context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.SelectedStepChanged.InvokeAsync("agent"));

        var step = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("Welcome Agent", step.DisplayName);
        Assert.AreEqual("welcome-agent", step.Agent.ResourceId);
    }

    [TestMethod]
    public void EditablePaletteUsesOneGenericToolCardAndShowsTheSelectedSchema()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Tool", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        var toolLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(toolLink.TextContent, "Send notification");
        Assert.AreEqual("/tools/notification.send", toolLink.GetAttribute("href"));
        Assert.AreEqual("_blank", toolLink.GetAttribute("target"));
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Tool");
        });
        Assert.AreEqual("Send notification", rendered.Find(".tool-picker-trigger span").TextContent);
        Assert.HasCount(2, rendered.FindAll(".tool-picker-option"));
        Assert.AreEqual("notification.send", rendered.Find(".inspector-resource-id code").TextContent);

        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        StringAssert.Contains(rendered.Markup, "message");
        rendered.Find("[data-testid='tool-pass-transition-output']").Change(false);
        rendered.Find(".flow-mapping-editor input:not([type='checkbox'])").Change("hello");
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        StringAssert.Contains(rendered.Markup, "deliveryId");
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll(".tool-picker-option").Single(option =>
            string.Equals(option.GetAttribute("aria-pressed"), "true", StringComparison.OrdinalIgnoreCase)).Click();
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());

        rendered.Find(".tool-picker-search input").Input("warehouse");
        Assert.HasCount(1, rendered.FindAll(".tool-picker-option"));
        rendered.Find(".tool-picker-option").Click();
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "requestId"));
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("warehouse.lookup", toolStep.Tool.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.tools"), toolStep.Tool.Namespace);
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        Assert.AreEqual("warehouse.lookup", rendered.Find(".inspector-resource-id code").TextContent);
    }

    [TestMethod]
    public async Task PublishedToolStepShowsItsSelectionWithoutEditableOptions()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ToolFlowStepDefinition
                {
                    Name = "tool",
                    Tool = new("notification.send", ResourceNamespace.Default),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new { })
                }
            ],
            Transitions = [new("input-tool", "input", "completed", "tool")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: true, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, new ResourceNamespace("pack.sample")));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("tool"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        Assert.AreEqual("Send notification", rendered.Find(".tool-picker-trigger.is-readonly span").TextContent);
        Assert.HasCount(0, rendered.FindAll(".tool-picker-option"));
    }

    [TestMethod]
    public async Task ExistingFlowCardLoadsItsContractWhenSelectedAndPersistsPassthrough()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "analyze", DisplayName = "Analyze news", Agent = new("news-agent") },
                new FlowCallStepDefinition
                {
                    Name = "deliver",
                    Flow = new("analysis", Namespace: new("pack.news")),
                    InputMapping = JsonSerializer.SerializeToElement(new { })
                }
            ],
            Transitions =
            [
                new("input-analyze", "input", "completed", "analyze"),
                new("analyze-deliver", "analyze", "success", "deliver")
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
            .Add(component => component.ResourceId, "parent"));
        var canvas = rendered.FindComponent<FlowCanvas>();
        await rendered.InvokeAsync(() => canvas.Instance.StepSettingsRequested.InvokeAsync("deliver"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='flow-pass-transition-output']")));
        rendered.Find("[data-testid='flow-pass-transition-output']").Change(true);
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Name == "deliver"));
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
    }

    [TestMethod]
    public void FlowCallNamedPortsAreResolvedAtInitialLoadWithoutSelectingTheNode()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition { Name = "deliver", Flow = new("analysis", Namespace: new("pack.news")) },
                new OutputFlowStepDefinition { Name = "done" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("input-deliver", "input", "completed", "deliver"),
                new("old-success-id", "deliver", "completed", "done"),
                new("deliver-approved-done", "deliver", "approved", "done"),
                new("unexpected-id", "deliver", "failure", "error"),
                new("deliver-rejected-error", "deliver", "rejected", "error")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();

        rendered.WaitForAssertion(() =>
        {
            var node = store.State.Diagram.Nodes.Single(item => item.Name == "deliver");
            CollectionAssert.AreEqual(new[] { "approved", "rejected" }, node.OutputEvents.ToArray());
            Assert.AreEqual(FlowOutputOutcome.Error, node.OutputOutcomes["rejected"]);
            Assert.IsFalse(store.State.Resource!.Definition.Transitions.Any(transition => transition.Event == "failure"));
            Assert.IsFalse(store.State.Resource.Definition.Transitions.Any(transition => transition.Event == "completed" && transition.FromStep == "deliver"));
            Assert.HasCount(2, store.State.Resource.Definition.Transitions.Where(transition => transition.FromStep == "deliver"));
            var renderedNode = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance).Nodes
                .OfType<FlowDiagramNode>()
                .Single(node => node.Source.Name == "deliver");
            Assert.HasCount(2, renderedNode.Outputs);
        });

        rendered.Find("[data-testid='flow-designer-validation-run']").Click();
        rendered.WaitForAssertion(() =>
        {
            var backend = context.Services.GetRequiredService<IFlowDesignerBackend>() as BackendStub;
            Assert.IsNotNull(backend);
            Assert.AreEqual(1, backend.ValidationCount);
            Assert.IsFalse(backend.LastSavedDefinition!.Transitions.Any(transition => transition.Event == "failure"));
            Assert.IsFalse(backend.LastSavedDefinition.Transitions.Any(transition => transition.Event == "completed" && transition.FromStep == "deliver"));
        });
    }

    [TestMethod]
    public async Task ChangingExactFlowVersionReplacesTheNamedPorts()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps = [new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) }, new OutputFlowStepDefinition { Name = "done" }]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] =
                [
                    new("1.0.0", null, null, [new("accepted", null, FlowOutputOutcome.Success, null)]),
                    new("2.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null), new("failed", null, FlowOutputOutcome.Error, null)])
                ]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "accepted" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray()));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        rendered.FindAll(".flow-inspector label").Single(element => element.TextContent.StartsWith("Exact version", StringComparison.Ordinal)).QuerySelector("select")!.Change("2.0.0");

        rendered.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "completed", "failed" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray()));
    }

    [TestMethod]
    public async Task ChangingCalledFlowRemovesAnAutomaticErrorRouteWhenNoErrorOutcomeRemains()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) },
                new OutputFlowStepDefinition { Name = "done" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("child-completed", "child", "completed", "done"),
                new("child-rejected", "child", "rejected", "error")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Flows =
            [
                new("analysis", "Analysis", new("pack.news"), "1.0.0"),
                new("summary", "Summary", new("pack.news"), "1.0.0")
            ],
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] = [new("1.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null), new("rejected", null, FlowOutputOutcome.Error, null)])],
                ["pack.news|summary"] = [new("1.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null)])]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => Assert.IsTrue(store.State.Resource!.Definition.Transitions.Any(transition => transition.Event == "rejected")));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        rendered.Find("[data-testid='flow-call-reference']").Change("pack.news|summary");

        rendered.WaitForAssertion(() =>
        {
            var transitions = store.State.Resource!.Definition.Transitions.Where(transition => transition.FromStep == "child").ToArray();
            Assert.HasCount(1, transitions);
            Assert.AreEqual("completed", transitions[0].Event);
            CollectionAssert.AreEqual(new[] { "completed" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray());
        });
    }

    [TestMethod]
    public async Task FlowCallInspectorShowsPublishedInputAndNamedOutputContracts()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var inputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var outputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) },
                new OutputFlowStepDefinition { Name = "result" }
            ],
            Transitions = [new("child-completed", "child", "completed", "result")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] =
                [
                    new("1.0.0", inputSchema, null,
                        [new("completed", "Completed", FlowOutputOutcome.Success, outputSchema)])
                ]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        var flowLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(flowLink.TextContent, "News analysis");
        Assert.AreEqual("/namespaces/pack.news/flows/analysis", flowLink.GetAttribute("href"));
        Assert.AreEqual("_blank", flowLink.GetAttribute("target"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("request").GetProperty("type").GetString());

        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        var namedOutput = rendered.Find("[data-testid='called-flow-output-contract']");
        StringAssert.Contains(namedOutput.TextContent, "Completed");
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("result"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task FlowCallCanReplaceAnExistingParentInputContractWithItsSharedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var reference = new SchemaVersionReference
        {
            Name = "analysis-request",
            Version = "1.2.0",
            Namespace = new("pack.news"),
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition
                {
                    Name = "input",
                    SchemaBinding = new SchemaBinding
                    {
                        Inline = new InlineSchemaBinding
                        {
                            Format = SchemaFormatIds.JsonSchema,
                            Dialect = "https://json-schema.org/draft/2020-12/schema",
                            Content = JsonSerializer.SerializeToElement(new { type = "string" })
                        }
                    }
                },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            SchemaVersions =
            [
                new("analysis-request", "Analysis request", new("pack.news"), reference.ScopeRef!.Value,
                    reference.Version, "json-schema", "https://json-schema.org/draft/2020-12/schema", schema)
            ],
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] = [new("1.0.0", schema, null, InputSchemaReference: reference)]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-child", "input", "completed", "child")));
        var connectionDialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(connectionDialog.TextContent, "Use the child Flow input contract?");
        StringAssert.Contains(connectionDialog.TextContent, "The current input contract will be replaced.");
        StringAssert.Contains(connectionDialog.TextContent, "Reusable shared contract");
        StringAssert.Contains(connectionDialog.TextContent, "pack.news/analysis-request · 1.2.0");
        CollectionAssert.Contains(connectionDialog.ParentElement!.ClassList.ToArray(), "connection-contract-backdrop");
        CollectionAssert.Contains(rendered.Find("[data-testid='connection-contract-apply']").ClassList.ToArray(), "button-primary");
        rendered.Find("[data-testid='connection-contract-apply']").Click();

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.HasCount(0, rendered.FindAll(".step-contract-source"));
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-shared-input-contract']").TextContent, "pack.news/analysis-request · 1.2.0");

        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.AreEqual(reference, input.SchemaBinding?.Reference);
        Assert.IsNull(input.Schema);
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='use-called-flow-shared-input']"));
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-shared-input-contract']").TextContent, "Parent input already configured");

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("input"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        StringAssert.Contains(rendered.Find("[data-testid='input-output-contract']").TextContent, "Same as input contract");
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("request").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task FlowCallCanCopyAnInlineInputContractIndependently()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0"),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["default|analysis"] = [new("1.0.0", schema, null)]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-child", "input", "completed", "child")));
        var connectionDialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(connectionDialog.TextContent, "Independent inline copy");
        StringAssert.Contains(connectionDialog.TextContent, "make it shared manually in the child Flow");
        rendered.Find("[data-testid='connection-contract-later']").Click();
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();

        var inlineContract = rendered.Find("[data-testid='flow-call-inline-input-contract']");
        StringAssert.Contains(inlineContract.TextContent, "later changes will not be synchronized");
        StringAssert.Contains(inlineContract.TextContent, "make it shared manually in the child Flow");
        CollectionAssert.Contains(rendered.Find("[data-testid='copy-called-flow-inline-input']").ClassList.ToArray(), "button-secondary");
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='use-called-flow-shared-input']"));

        rendered.Find("[data-testid='copy-called-flow-inline-input']").Click();

        var input = (InputFlowStepDefinition)context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .Single(step => step.Name == "input");
        Assert.IsNotNull(input.SchemaBinding?.Inline);
        Assert.IsNull(input.SchemaBinding.Reference);
        Assert.AreEqual(SchemaFormatIds.JsonSchema, input.SchemaBinding.Inline.Format);
        Assert.AreEqual("object", input.SchemaBinding.Inline.Content.GetProperty("type").GetString());
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-inline-input-contract']").TextContent, "Parent input already configured");
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='copy-called-flow-inline-input']"));
    }

    [TestMethod]
    public async Task AgentConnectionCanReplaceTheFlowInputWithItsSharedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { prompt = new { type = "string" } } });
        var reference = new SchemaVersionReference
        {
            Name = "agent-request",
            Version = "2.1.0",
            Namespace = new("pack.agents"),
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = JsonSerializer.SerializeToElement(new { type = "string" }) },
                new AgentFlowStepDefinition
                {
                    Name = "writer",
                    Agent = new("writer", Namespace: new("pack.agents")),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new FlowDesignerAgent("writer", "Editorial writer")
                {
                    Namespace = new("pack.agents"),
                    InputSchema = schema,
                    InputSchemaReference = reference
                }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-writer", "input", "completed", "writer")));

        var dialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(dialog.TextContent, "Use the Agent input contract?");
        StringAssert.Contains(dialog.TextContent, "Editorial writer");
        StringAssert.Contains(dialog.TextContent, "The current input contract will be replaced.");
        StringAssert.Contains(dialog.TextContent, "pack.agents/agent-request · 2.1.0");
        rendered.Find("[data-testid='connection-contract-apply']").Click();

        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.AreEqual(reference, input.SchemaBinding?.Reference);
        Assert.IsNull(input.Schema);
    }

    [TestMethod]
    public async Task ToolConnectionCanDeferAnIndependentInlineContractCopy()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ToolFlowStepDefinition
                {
                    Name = "notify",
                    Tool = new("notification.send"),
                    ArgumentsMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-notify", "input", "completed", "notify")));

        var dialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(dialog.TextContent, "Use the Tool input contract?");
        StringAssert.Contains(dialog.TextContent, "Send notification");
        StringAssert.Contains(dialog.TextContent, "later changes will not be synchronized");
        rendered.Find("[data-testid='connection-contract-later']").Click();

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(input.Schema);
        Assert.IsNull(input.SchemaBinding);
    }

    [TestMethod]
    public async Task AgentConnectionDoesNotPromptWhenItsMappingIsNotAPassthrough()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition
                {
                    Name = "writer",
                    Agent = new("writer"),
                    InputMapping = JsonSerializer.SerializeToElement(new { prompt = "${transition.output.prompt}" })
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new FlowDesignerAgent("writer", "Writer")
                {
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-writer", "input", "completed", "writer")));

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
    }

    [TestMethod]
    public async Task OutputInspectorShowsCanonicalMappingAndInheritedSchemaAndEditsErrorDetails()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { result = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = schema },
                new OutputFlowStepDefinition { Name = "result", Outcome = FlowOutputOutcome.Error, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }
            ],
            Transitions = [new("input-result", "input", "completed", "result")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("result"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();

        StringAssert.Contains(rendered.Find("[data-testid='terminal-output-contract']").TextContent, "Same as input contract");
        var mapping = rendered.Find("[data-testid='output-mapping']");
        Assert.IsEmpty(mapping.QuerySelectorAll("input, textarea"));
        Assert.AreEqual("${transition.output}", mapping.QuerySelector("code")!.TextContent);
        StringAssert.Contains(mapping.TextContent, "Incoming transition output");
        Assert.AreEqual("object", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value.GetProperty("type").GetString());
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll(".flow-inspector input").Single(element => element.ParentElement!.TextContent.Contains("Error details expression", StringComparison.Ordinal)).Change("${transition.output.details}");

        var output = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps.OfType<OutputFlowStepDefinition>().Single();
        Assert.AreEqual(JsonValueKind.String, output.OutputMapping?.ValueKind);
        Assert.AreEqual("${transition.output}", output.OutputMapping?.GetString());
        Assert.IsNull(output.Schema);
        Assert.IsNull(output.SchemaBinding);
        Assert.AreEqual("${transition.output.details}", output.DetailsExpression);
    }

    [TestMethod]
    public async Task TransformInspectorPinsAnExactPublishedSharedOutputSchemaVersion()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition { Name = "transform", Mapping = JsonSerializer.SerializeToElement("${transition.output}") },
                new OutputFlowStepDefinition { Name = "result" }
            ],
            Transitions =
            [
                new("input-transform", "input", "completed", "transform"),
                new("transform-result", "transform", "completed", "result")
            ]
        };
        var scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            SchemaVersions =
            [
                new("task-result", "Task result", ResourceNamespace.Default, scope, "1.2.0", "json-schema",
                    "https://json-schema.org/draft/2020-12/schema",
                    JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } }))
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("transform"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();

        rendered.Find("[data-testid='transform-output-schema-start-shared']").Click();

        var transform = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<TransformFlowStepDefinition>().Single();
        Assert.IsNull(transform.Schema);
        Assert.AreEqual("task-result", transform.SchemaBinding?.Reference?.Name);
        Assert.AreEqual("1.2.0", transform.SchemaBinding?.Reference?.Version);
        Assert.AreEqual(scope, transform.SchemaBinding?.Reference?.ScopeRef);

        rendered.Find("[data-testid='transform-output-schema-remove']").Click();

        transform = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<TransformFlowStepDefinition>().Single();
        Assert.IsNull(transform.Schema);
        Assert.IsNull(transform.SchemaBinding);
        StringAssert.Contains(rendered.Find("[data-testid='transform-output-schema-empty']").TextContent, "No contract is configured");
    }

    [TestMethod]
    public async Task InputInspectorCanRemoveALegacyRootContractAndCreateANewInlineContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = schema,
            Steps = [new InputFlowStepDefinition { Name = "input" }],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("input"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.HasCount(0, rendered.FindAll(".step-contract-source"));
        rendered.Find("[data-testid='flow-input-schema-remove']").Click();

        var updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        var input = updated.Steps.OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(updated.InputSchema);
        Assert.IsNull(updated.InputSchemaBinding);
        Assert.IsNull(input.Schema);
        Assert.IsNull(input.SchemaBinding);
        StringAssert.Contains(rendered.Find("[data-testid='flow-input-schema-empty']").TextContent, "No contract is configured");

        rendered.Find("[data-testid='flow-input-schema-start-inline']").Click();

        updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        input = updated.Steps.OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(updated.InputSchema);
        Assert.IsNotNull(input.SchemaBinding?.Inline);
        Assert.AreEqual("object", input.SchemaBinding.Inline.Content.GetProperty("type").GetString());
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

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(new UserPreferencesState(new PreferencesClientStub()));
        return context;
    }

    private static BlazorDiagram GetDiagram(FlowCanvas canvas) =>
        (BlazorDiagram)typeof(FlowCanvas).GetField("diagram", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    private sealed class PreferencesClientStub : IUserPreferencesClient
    {
        public Task<UserPreferences> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferences(UserTheme.Light, null, DateTimeOffset.UnixEpoch));

        public Task<UserPreferences> UpdateAsync(UserTheme theme, string? language, CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferences(theme, language, DateTimeOffset.UnixEpoch));
    }

    private sealed class ResourceProviderStub : IFlowDesignerResourceProvider
    {
        private readonly IReadOnlyList<string> publishedVersions;
        public IReadOnlyList<FlowDesignerAgent> Agents { get; init; } = [];
        public IReadOnlyList<FlowDesignerFlow> Flows { get; init; } = [new("analysis", "News analysis", new("pack.news"), "2.0.0")];
        public IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerFlowVersion>> FlowVersions { get; init; } = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal);
        public IReadOnlyList<FlowDesignerSchemaVersion> SchemaVersions { get; init; } = [];
        public IReadOnlyList<FlowDesignerTool> Tools { get; init; } =
        [
            new(
                "notification.send",
                "Send notification",
                ResourceNamespace.Default,
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { deliveryId = new { type = "string" } } }),
                Enabled: true,
                Available: true,
                RequiresApproval: false),
            new(
                "warehouse.lookup",
                "Look up warehouse",
                new("pack.tools"),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { requestId = new { type = "string" } } }),
                null,
                Enabled: true,
                Available: true,
                RequiresApproval: false)
        ];

        public ResourceProviderStub(params string[] publishedVersions) => this.publishedVersions = publishedVersions;

        public Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Agents);
        public Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Flows);
        public Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            FlowVersions.TryGetValue($"{@namespace.Value}|{name}", out var configured)
                ? Task.FromResult(configured)
                : name == "sample" && @namespace.IsDefault
                ? Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>(publishedVersions.Select(version => new FlowDesignerFlowVersion(version, null, null)).ToArray())
                : Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([new("2.0.0",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { summary = new { type = "string" } } }),
                [new("approved", "Approved", FlowOutputOutcome.Success, null), new("rejected", "Rejected", FlowOutputOutcome.Error, null)])]);
        public Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Tools);
        public Task<IReadOnlyList<FlowDesignerSchemaVersion>> GetSchemaVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SchemaVersions);
    }

    private sealed class BackendStub : IFlowDesignerBackend
    {
        private readonly bool readOnly;
        private FlowDraftResponse draft;
        private string source = "entryStep: input";
        public BackendStub(bool readOnly = true, FlowGraphDefinition? definition = null)
        {
            this.readOnly = readOnly;
            draft = CreateDraft(definition);
        }
        public int SaveCount { get; private set; }
        public int ValidationCount { get; private set; }
        public FlowGraphDefinition? LastSavedDefinition { get; private set; }
        public string? LastReplacementSource { get; private set; }
        public string? ReplacementError { get; set; }
        public FlowGraphDefinition? ReplacementDefinition { get; set; }
        public bool VersionConflict { get; set; }
        public string? LastPublishVersion { get; private set; }
        public FlowDesignerTarget? LoadedTarget { get; private set; }
        public Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            LoadedTarget = target;
            var value = draft.Value;
            return Task.FromResult(new FlowDesignerLoadResult(new(value.FlowId, value.DisplayName, value.Description, value.Tags, value.Definition), source, ETag: readOnly ? null : draft.ETag, PublishedVersion: readOnly ? "2.1.0" : null));
        }
        public Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new FlowSourceResponse(source, "yaml", draft.Value.Revision));
        public Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
        {
            SaveCount++;
            LastSavedDefinition = request.Definition;
            draft = new(draft.Value with { Definition = request.Definition, Revision = draft.Value.Revision + 1 }, $"\"etag-{draft.Value.Revision + 1}\"");
            return Task.FromResult(draft);
        }
        public Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
        {
            LastReplacementSource = request.Source;
            if (ReplacementError is not null) throw new InvalidOperationException(ReplacementError);
            source = request.Source;
            draft = new(draft.Value with { Definition = ReplacementDefinition ?? draft.Value.Definition, Revision = draft.Value.Revision + 1 }, "\"next-etag\"");
            return Task.FromResult(draft);
        }
        public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            ValidationCount++;
            return Task.FromResult(new FlowValidationResponse(true, []));
        }
        public Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken)
        {
            LastPublishVersion = request.Version;
            if (VersionConflict)
                throw new FlowDesignerVersionAlreadyPublishedException(request.Version, new InvalidOperationException("duplicate"));
            return Task.FromResult(new FlowVersionResponse(target.ResourceId, request.Version, null,
                new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id")),
                new Dictionary<string, string>(), DateTimeOffset.UnixEpoch));
        }
        public Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static FlowDraftResponse CreateDraft(FlowGraphDefinition? definition = null)
        {
            var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            definition ??= new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
            return new(new FlowDraft { WorkspaceId = WorkspaceId, Id = "draft", FlowId = new("sample"), DisplayName = "Sample", Definition = definition, CreatedAt = now, UpdatedAt = now }, "\"etag\"");
        }

        private static readonly Agentstration.Resources.WorkspaceId WorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
