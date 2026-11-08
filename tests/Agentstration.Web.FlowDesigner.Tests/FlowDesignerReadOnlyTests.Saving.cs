using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    private static void ConfigureSavingContext(BunitContext context, BackendStub backend)
    {
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task AutosaveAcknowledgementPreservesAnEndpointBeingDragged()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new TransformFlowStepDefinition { Name = "transform" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("route", "input", "completed", "transform")]
        };
        var backend = new BackendStub(false, definition) { BeforeSave = async () => { started.TrySetResult(true); await release.Task; } };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        var diagram = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance);
        var link = diagram.Links.Single();
        var output = diagram.Nodes.OfType<FlowDiagramNode>().Single(node => node.Source.Name == "output");
        var save = rendered.InvokeAsync(() => InvokeCoordinatorAsync(rendered.Instance, includeSource: false));
        await started.Task;
        var dragging = new PositionAnchor(new Point(500, 500));
        await rendered.InvokeAsync(() => link.SetTarget(dragging));
        release.SetResult(true);
        await save;

        Assert.AreSame(link, diagram.Links.Single());
        Assert.AreSame(dragging, link.Target);
        Assert.IsFalse(store.State.IsDirty);
        await rendered.InvokeAsync(() => link.SetTarget(new SinglePortAnchor(output.Input)));
        Assert.AreEqual("output", store.State.Resource!.Definition.Transitions.Single().ToStep);
    }

    [TestMethod]
    public async Task SavingFlushesEditsMadeDuringWriteAndSerializesValidation()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var backend = new BackendStub(false) { BeforeSave = async () => { started.TrySetResult(true); await release.Task; } };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        var save = rendered.Find("[data-testid='flow-designer-save']").ClickAsync(new MouseEventArgs());
        await started.Task;
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(30, 40))));
        var autosave = rendered.InvokeAsync(() => InvokeCoordinatorAsync(rendered.Instance, includeSource: false));
        var validate = rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "ValidateAsync"));
        Assert.AreEqual(0, backend.ValidationCount);
        Assert.AreEqual(1, backend.SaveCount);
        Assert.IsTrue(store.State.IsDirty);
        release.SetResult(true);
        await Task.WhenAll(save, autosave, validate);
        Assert.AreEqual(2, backend.SaveCount);
        CollectionAssert.AreEqual(new[] { "\"etag\"", "\"etag-2\"" }, backend.SavedETags);
        Assert.AreEqual(new FlowNodePosition(30, 40), backend.LastSavedDefinition!.Designer.NodePositions["input"]);
        Assert.IsFalse(store.State.IsDirty);
        Assert.AreEqual(1, backend.ValidationCount);
    }

    [TestMethod]
    public async Task AutosaveDoesNotOverwriteYamlTypedDuringVisualSave()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var backend = new BackendStub(false) { BeforeSave = async () => { started.TrySetResult(true); await release.Task; } };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        // Enter the same coordinator as the debounce callback, without waiting for a timer.
        var save = rendered.InvokeAsync(() => InvokeCoordinatorAsync(rendered.Instance, includeSource: false));
        await started.Task;
        await rendered.InvokeAsync(() => store.SetSource("entryStep: [unfinished", null));
        release.SetResult(true);
        await save;
        Assert.AreEqual("entryStep: [unfinished", store.State.SourceText);
        Assert.IsTrue(store.State.HasSourceChanges);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(1, backend.SaveCount);
        Assert.IsNull(backend.LastReplacementSource);
    }

    [TestMethod]
    public async Task ApplyingYamlPreservesAndSavesNewerTypingBeforePublishing()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var backend = new BackendStub(false) { BeforeReplace = async () => { started.TrySetResult(true); await release.Task; } };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.SetSource("entryStep: input # first", null));
        var save = rendered.Find("[data-testid='flow-designer-save']").ClickAsync(new MouseEventArgs());
        await started.Task;
        await rendered.InvokeAsync(() => store.SetSource("entryStep: input # latest", null));
        var publish = rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "PublishAsync"));
        Assert.IsNull(backend.LastPublishVersion);
        release.SetResult(true);
        await Task.WhenAll(save, publish);
        Assert.AreEqual("entryStep: input # latest", backend.LastReplacementSource);
        Assert.AreEqual("entryStep: input # latest", store.State.SourceText);
        Assert.IsFalse(store.State.IsDirty);
        Assert.IsNotNull(backend.LastPublishVersion);
        Assert.HasCount(2, backend.SavedETags);
    }

    [TestMethod]
    public async Task FailedSavePreservesDraftAndBlocksValidationAndPublication()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false) { BeforeSave = () => throw new InvalidOperationException("ETag conflict") };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "ValidateAsync"));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "PublishAsync"));
        Assert.AreEqual(0, backend.ValidationCount);
        Assert.IsNull(backend.LastPublishVersion);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(FlowSaveState.SaveFailed, store.State.SaveState);
        Assert.AreEqual(new FlowNodePosition(10, 20), store.State.Resource!.Definition.Designer.NodePositions["input"]);
        StringAssert.Contains(rendered.Markup, "ETag conflict");
    }

    [TestMethod]
    public async Task SourceReadFailureKeepsAcknowledgedEtagForRetry()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false) { ReadSource = _ => throw new InvalidOperationException("Source unavailable") };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        Assert.AreEqual("\"etag-2\"", store.State.ETag);
        Assert.IsTrue(store.State.IsDirty);
        backend.ReadSource = null;
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        CollectionAssert.AreEqual(new[] { "\"etag\"", "\"etag-2\"" }, backend.SavedETags);
        Assert.IsFalse(store.State.IsDirty);
    }

    [TestMethod]
    public async Task LateSourceResponseCannotOverwriteSameFlowInAnotherScope()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = new TaskCompletionSource<FlowSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BackendStub(false) { ReadSource = _ => { started.TrySetResult(true); return release.Task; } };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample").Add(c => c.ScopeRef, TestScope));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        var save = rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        await started.Task;
        var otherScope = ResourceScopeRef.Workspace(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        rendered.Render(p => p.Add(c => c.ResourceId, "sample").Add(c => c.ScopeRef, otherScope));
        await rendered.InvokeAsync(() => store.SetSource("new scope source", null));
        release.SetResult(new("old scope source", "yaml", 1));
        await save;
        Assert.AreEqual("new scope source", store.State.SourceText);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(otherScope, backend.LoadedTarget!.ScopeRef);
    }

    [TestMethod]
    public async Task MismatchedCanonicalRevisionPreservesDraftAndBlocksPublication()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var backend = new BackendStub(false) { ReadSource = _ => Task.FromResult(new FlowSourceResponse("someone else's source", "yaml", 99)) };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "PublishAsync"));
        Assert.IsNull(backend.LastPublishVersion);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual("entryStep: input", store.State.SourceText);
        Assert.AreEqual(FlowSaveState.SaveFailed, store.State.SaveState);
        StringAssert.Contains(rendered.Markup, "draft changed while saving");
    }

    [TestMethod]
    public async Task LateWriteFailureDoesNotMarkAnotherFlowAsFailed()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var backend = new BackendStub(false)
        {
            BeforeSave = async () => { started.TrySetResult(true); await release.Task; throw new InvalidOperationException("Old Flow conflict"); }
        };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        var save = rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        await started.Task;
        rendered.Render(p => p.Add(c => c.ResourceId, "another-flow"));
        release.SetResult(true);
        await save;
        Assert.AreEqual(FlowSaveState.Saved, store.State.SaveState);
        Assert.IsFalse(store.State.IsDirty);
        Assert.DoesNotContain("Old Flow conflict", rendered.Markup);
    }

    [TestMethod]
    public async Task FailedYamlApplyDoesNotAttachOldDiagnosticToNewerTyping()
    {
        using var context = CreateContext();
        var started = Signal();
        var release = Signal();
        var backend = new BackendStub(false)
        {
            BeforeReplace = async () => { started.TrySetResult(true); await release.Task; },
            ReplacementError = "Invalid old YAML"
        };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.SetSource("invalid first YAML", null));
        var save = rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        await started.Task;
        await rendered.InvokeAsync(() => store.SetSource("corrected YAML", null));
        release.SetResult(true);
        await save;
        Assert.AreEqual("corrected YAML", store.State.SourceText);
        Assert.IsNull(store.State.SourceError);
        Assert.IsTrue(store.State.IsDirty);
        StringAssert.Contains(rendered.Markup, "Invalid old YAML");
    }

    [TestMethod]
    public async Task FailedTargetLoadCannotSavePreviousFlowsDirtyDraft()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false) { FailingLoadTarget = "unavailable" };
        ConfigureSavingContext(context, backend);
        var rendered = context.Render<FlowDesignerComponent>(p => p.Add(c => c.ResourceId, "sample"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await rendered.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 20))));
        rendered.Render(p => p.Add(c => c.ResourceId, "unavailable"));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "SaveAsync"));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "ValidateAsync"));
        await rendered.InvokeAsync(() => InvokeDesignerAsync(rendered.Instance, "PublishAsync"));
        Assert.AreEqual(0, backend.SaveCount);
        Assert.AreEqual(0, backend.ValidationCount);
        Assert.IsNull(backend.LastPublishVersion);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(new FlowNodePosition(10, 20), store.State.Resource!.Definition.Designer.NodePositions["input"]);
        StringAssert.Contains(rendered.Markup, "Flow load unavailable");
    }

    private static Task InvokeDesignerAsync(FlowDesignerComponent component, string method) =>
        (Task)typeof(FlowDesignerComponent).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(component, null)!;

    private static Task InvokeCoordinatorAsync(FlowDesignerComponent component, bool includeSource) =>
        (Task)typeof(FlowDesignerComponent).GetMethod("WithSavedDraftAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(component, [null, includeSource, CancellationToken.None])!;
}
