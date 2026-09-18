using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using AngleSharp.Dom;
using Blazor.Diagrams;
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
                new("agent-output", "agent", "completed", "output")
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
        Assert.HasCount(9, rendered.FindAll(".step-palette-group:first-child button .ui-icon"));
        Assert.HasCount(3, rendered.FindAll(".palette-view button .ui-icon"));

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
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Flow", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Flow");
            StringAssert.Contains(rendered.Markup, "article");
            StringAssert.Contains(rendered.Markup, "summary");
        });
        Assert.AreEqual("/namespaces/pack.news/flows/analysis", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Flow ID");

        rendered.Find("[data-testid='flow-pass-transition-output']").Change(true);
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.String, call.InputMapping?.ValueKind);
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
        StringAssert.Contains(rendered.Markup, "Pass incoming transition output");

        rendered.Find("[data-testid='flow-pass-transition-output']").Change(false);
        call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.Object, call.InputMapping?.ValueKind);
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "article"));
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

        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => Assert.AreEqual("Welcome Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        var step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("welcome-agent", step.Agent.ResourceId);
        Assert.AreEqual("/agents/welcome-agent", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-details-group").TextContent, "Step ID");
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Agent ID");

        rendered.Find(".flow-inspector select").Change("pack.demo|packed-agent");
        rendered.WaitForAssertion(() => Assert.AreEqual("Pack Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("packed-agent", step.Agent.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.demo"), step.Agent.Namespace);
        Assert.AreEqual("/namespaces/pack.demo/agents/packed-agent", rendered.Find(".inspector-resource-link").GetAttribute("href"));
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
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Tool");
            StringAssert.Contains(rendered.Markup, "message");
            StringAssert.Contains(rendered.Markup, "deliveryId");
        });
        Assert.AreEqual("Send notification", rendered.Find(".tool-picker-trigger span").TextContent);
        Assert.HasCount(2, rendered.FindAll(".tool-picker-option"));
        Assert.AreEqual("notification.send", rendered.Find(".inspector-resource-id code").TextContent);

        rendered.Find(".flow-mapping-editor input").Change("hello");
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());
        rendered.FindAll(".tool-picker-option").Single(option =>
            string.Equals(option.GetAttribute("aria-pressed"), "true", StringComparison.OrdinalIgnoreCase)).Click();
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());

        rendered.Find(".tool-picker-search input").Input("warehouse");
        Assert.HasCount(1, rendered.FindAll(".tool-picker-option"));
        rendered.Find(".tool-picker-option").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "requestId"));
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("warehouse.lookup", toolStep.Tool.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.tools"), toolStep.Tool.Namespace);
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
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.SelectedStepChanged.InvokeAsync("tool"));

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
                new("analyze-deliver", "analyze", "completed", "deliver")
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
        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("deliver"));

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='flow-pass-transition-output']")));
        rendered.Find("[data-testid='flow-pass-transition-output']").Change(true);
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Name == "deliver"));
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
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

        public ResourceProviderStub(params string[] publishedVersions) => this.publishedVersions = publishedVersions;

        public Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Agents);
        public Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerFlow>>([new("analysis", "News analysis", new("pack.news"), "2.0.0")]);
        public Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            name == "sample" && @namespace.IsDefault
                ? Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>(publishedVersions.Select(version => new FlowDesignerFlowVersion(version, null, null)).ToArray())
                : Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([new("2.0.0",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { summary = new { type = "string" } } }))]);
        public Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerTool>>([new(
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
                    RequiresApproval: false)]);
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
        public Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) { SaveCount++; return Task.FromResult(draft); }
        public Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
        {
            LastReplacementSource = request.Source;
            if (ReplacementError is not null) throw new InvalidOperationException(ReplacementError);
            source = request.Source;
            draft = new(draft.Value with { Definition = ReplacementDefinition ?? draft.Value.Definition, Revision = draft.Value.Revision + 1 }, "\"next-etag\"");
            return Task.FromResult(draft);
        }
        public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
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
