using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.State;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowEditorStoreTests
{
    [TestMethod]
    public async Task CommandsKeepDiagramAndDefinitionSynchronizedAndSupportUndoRedo()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("input-output", "input", "completed", "output")]
        };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new AddStepCommand(new TransformFlowStepDefinition { Name = "transform" }, new(100, 200)));
        await store.DispatchAsync(new MoveStepCommand("transform", new(240, 320)));

        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(3, store.State.Diagram.Nodes.Count);
        Assert.AreEqual(new FlowNodePosition(240, 320), store.State.Resource!.Definition.Designer.NodePositions["transform"]);
        store.Undo();
        Assert.AreEqual(new FlowNodePosition(100, 200), store.State.Resource.Definition.Designer.NodePositions["transform"]);
        store.Undo();
        Assert.IsFalse(store.State.Resource.Definition.Steps.Any(step => step.Name == "transform"));
        store.Redo();
        Assert.IsTrue(store.State.Resource.Definition.Steps.Any(step => step.Name == "transform"));
    }

    [TestMethod]
    public async Task AddingStepWithTransitionIsOneUndoableChange()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }],
            Transitions = []
        };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new AddStepCommand(
            new AgentFlowStepDefinition { Name = "agent", Agent = new("sample") },
            new(300, 200),
            new("agent-error-error", "agent", "error", "error")));

        Assert.IsTrue(store.State.Resource!.Definition.Steps.Any(step => step.Name == "agent"));
        Assert.IsTrue(store.State.Resource.Definition.Transitions.Any(transition => transition.Id == "agent-error-error"));
        store.Undo();
        Assert.IsFalse(store.State.Resource.Definition.Steps.Any(step => step.Name == "agent"));
        Assert.IsFalse(store.State.Resource.Definition.Transitions.Any(transition => transition.Id == "agent-error-error"));
    }

    [TestMethod]
    public void AutoLayoutUsesGraphLayersAndSeparatesBranches()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new RouterFlowStepDefinition { Name = "router" },
                new AgentFlowStepDefinition { Name = "agent", Agent = new("/agents/main") },
                new FailureFlowStepDefinition { Name = "failure" },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions =
            [
                new("input-router", "input", "completed", "router"),
                new("router-agent", "router", "selected", "agent"),
                new("router-failure", "router", "failed", "failure"),
                new("agent-output", "agent", "success", "output"),
                new("agent-failure", "agent", "error", "failure")
            ]
        };

        var horizontal = new ApplyAutoLayoutCommand(false).Apply(definition);
        var vertical = new ApplyAutoLayoutCommand(true).Apply(definition);

        Assert.IsTrue(horizontal.Designer.NodePositions["input"].X < horizontal.Designer.NodePositions["router"].X);
        Assert.IsTrue(horizontal.Designer.NodePositions["agent"].X < horizontal.Designer.NodePositions["failure"].X);
        Assert.AreEqual(horizontal.Designer.NodePositions["output"].X, horizontal.Designer.NodePositions["failure"].X);
        Assert.AreNotEqual(horizontal.Designer.NodePositions["output"].Y, horizontal.Designer.NodePositions["failure"].Y);
        Assert.IsTrue(vertical.Designer.NodePositions["input"].Y < vertical.Designer.NodePositions["router"].Y);
        Assert.IsTrue(vertical.Designer.NodePositions["agent"].Y < vertical.Designer.NodePositions["failure"].Y);
        Assert.AreEqual(vertical.Designer.NodePositions["output"].Y, vertical.Designer.NodePositions["failure"].Y);
        Assert.AreNotEqual(vertical.Designer.NodePositions["output"].X, vertical.Designer.NodePositions["failure"].X);
        Assert.AreEqual("Horizontal", horizontal.Designer.PreferredLayout);
        Assert.AreEqual("Vertical", vertical.Designer.PreferredLayout);
    }

    [TestMethod]
    public async Task ReconnectingTransitionUpdatesItsEventAndPreservesMetadata()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition { Name = "transform" },
                new OutputFlowStepDefinition { Name = "output" }
            ],
            Transitions = [new("route", "input", "matched", "output", "${input.ready}", 7)]
        };
        var store = new FlowEditorStore();
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new ReconnectTransitionCommand("route", "transform", "output", "completed"));

        var transition = store.State.Resource!.Definition.Transitions.Single();
        Assert.AreEqual("transform", transition.FromStep);
        Assert.AreEqual("output", transition.ToStep);
        Assert.AreEqual("completed", transition.Event);
        Assert.AreEqual("${input.ready}", transition.Condition);
        Assert.AreEqual(7, transition.Priority);
        store.Undo();
        Assert.AreEqual("input", store.State.Resource.Definition.Transitions.Single().FromStep);
        store.Redo();
        Assert.AreEqual("transform", store.State.Resource.Definition.Transitions.Single().FromStep);
    }

    [TestMethod]
    public void ReconcileFlowCallTransitionsReplacesOrRemovesLegacyAutomaticErrorEvent()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("nested") },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("child-error-error", "child", "error", "error"),
                new("child-failed-error", "child", "failed", "error")
            ]
        };
        var outputs = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal)
        {
            ["child"] = [new("done", "Done", FlowOutputOutcome.Success, null), new("failed", "Failed", FlowOutputOutcome.Error, null)]
        };

        var reconciled = new ReconcileFlowCallTransitionsCommand(outputs).Apply(definition);

        Assert.HasCount(1, reconciled.Transitions);
        Assert.AreEqual("failed", reconciled.Transitions.Single().Event);

        var replaced = new ReconcileFlowCallTransitionsCommand(outputs).Apply(definition with
        {
            Transitions = [new("child-error-error", "child", "error", "error")]
        });
        Assert.AreEqual("failed", replaced.Transitions.Single().Event);
        Assert.AreEqual("child-error-error", replaced.Transitions.Single().Id);
    }

    [TestMethod]
    public async Task UpdatingTransitionChangesMetadataWithoutReplacingItsIdentity()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }],
            Transitions = [new("route", "input", "completed", "output")]
        };
        var store = new FlowEditorStore();
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new UpdateTransitionCommand(new("route", "input", "approved", "output", "${input.approved}", 2)));

        var transition = store.State.Resource!.Definition.Transitions.Single();
        Assert.AreEqual("route", transition.Id);
        Assert.AreEqual("approved", transition.Event);
        Assert.AreEqual("${input.approved}", transition.Condition);
        Assert.AreEqual(2, transition.Priority);
    }

    [TestMethod]
    public void RenamingOutputUpdatesReferencesPositionAndEntryAtomically()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "output",
            Steps = [new OutputFlowStepDefinition { Name = "output" }, new InputFlowStepDefinition { Name = "input" }],
            Transitions = [new("route", "input", "completed", "output")],
            Designer = new FlowDesignerMetadata { NodePositions = new Dictionary<string, FlowNodePosition> { ["output"] = new(12, 24) } }
        };
        var renamed = new RenameStepCommand("output", "completed").Apply(definition);
        Assert.AreEqual("completed", renamed.EntryStep);
        Assert.AreEqual("completed", renamed.Transitions.Single().ToStep);
        Assert.AreEqual(new FlowNodePosition(12, 24), renamed.Designer.NodePositions["completed"]);
        Assert.IsFalse(renamed.Designer.NodePositions.ContainsKey("output"));
    }

    [TestMethod]
    public void NamedFlowCallOutputsSurviveSavedStateReprojection()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps = [new FlowCallStepDefinition { Name = "child", Flow = new("nested") }, new OutputFlowStepDefinition { Name = "done" }],
            Transitions = []
        };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var response = new FlowDraftResponse(draft, "\"etag-1\"");
        var store = new FlowEditorStore();
        store.Load(response, "entryStep: child");
        store.SetFlowCallOutputs("child",
        [
            new("approved", "Approved", FlowOutputOutcome.Success, null),
            new("rejected", "Rejected", FlowOutputOutcome.Error, null)
        ]);

        store.MarkSaved(response, "entryStep: child");

        var node = store.State.Diagram.Nodes.Single(item => item.Name == "child");
        CollectionAssert.AreEqual(new[] { "approved", "rejected" }, node.OutputEvents.ToArray());
        Assert.AreEqual(FlowOutputOutcome.Error, node.OutputOutcomes["rejected"]);
    }

    [TestMethod]
    public async Task RejectedRenameDoesNotDirtyTheStore()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }] };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new RenameStepCommand("output", "input"));

        Assert.IsFalse(store.State.IsDirty);
        Assert.IsFalse(store.CanUndo);
        Assert.AreEqual("output", store.State.Resource!.Definition.Steps.OfType<OutputFlowStepDefinition>().Single().Name);
    }

    [TestMethod]
    public async Task AcceptedRenameMovesTheCurrentSelectionInTheSameStateChange()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }, new OutputFlowStepDefinition { Name = "output" }] };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");
        store.SelectStep("output");

        await store.DispatchAsync(new RenameStepCommand("output", "completed"));

        Assert.AreEqual("completed", store.State.Selection.StepName);
        Assert.AreEqual("completed", store.State.Resource!.Definition.Steps.OfType<OutputFlowStepDefinition>().Single().Name);
    }

    [TestMethod]
    public async Task PublishedNamespacedDocumentRejectsCommands()
    {
        var definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        var store = new FlowEditorStore();
        store.Load(new FlowDesignerLoadResult(new(new("sample"), "Sample", null, new Dictionary<string, string>(), definition), "entryStep: input", PublishedVersion: "1.0.0"), new(new ResourceNamespace("pack.sample"), "sample"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.DispatchAsync(new MoveStepCommand("input", new(10, 10))));
        Assert.IsFalse(store.State.IsDirty);
        Assert.IsTrue(store.State.IsReadOnly);
    }

    private static readonly Agentstration.Resources.WorkspaceId WorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
}
