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
    public async Task PublishVersionStartsAfterTheLatestPublishedVersionAndSurvivesSave()
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

        await rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Publish").ClickAsync(new());
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("1.0.11", backend.LastPublishVersion);
            StringAssert.Contains(rendered.Find("[data-testid='flow-designer-publication-success']").TextContent, "Version 1.0.11 was published and activated.");
        });
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
}
