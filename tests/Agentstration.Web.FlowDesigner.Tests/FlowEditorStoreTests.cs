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
    public async Task SaveAcknowledgesOnlyCapturedGraphAndPreservesUndoRedo()
    {
        var response = SavingDraft();
        var store = new FlowEditorStore();
        store.Load(response, "initial source");
        await store.DispatchAsync(new MoveStepCommand("input", new(10, 20)));
        var sent = store.CaptureSave();
        await store.DispatchAsync(new MoveStepCommand("input", new(30, 40)));
        var saved = new FlowDraftResponse(response.Value with { Definition = sent.Resource.Definition, Revision = 1 }, "next-etag");
        store.MarkSaved(saved, "saved source", sent);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual("next-etag", store.State.ETag);
        Assert.AreEqual(new FlowNodePosition(30, 40), store.State.Resource!.Definition.Designer.NodePositions["input"]);
        Assert.AreEqual(FlowSaveState.UnsavedChanges, store.State.SaveState);
        var latest = store.CaptureSave();
        store.Undo();
        store.Redo();
        store.MarkSaved(new(saved.Value with { Definition = latest.Resource.Definition }, "newer-etag"), "latest source", latest);
        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(new FlowNodePosition(30, 40), store.State.Resource.Definition.Designer.NodePositions["input"]);
        Assert.IsTrue(store.CanUndo);
    }

    [TestMethod]
    public void LateSaveDoesNotReplaceNewerRawSourceOrClearItsDirtyState()
    {
        var response = SavingDraft();
        var store = new FlowEditorStore();
        store.Load(response, "initial source");
        store.SetSource("first YAML", null);
        var snapshot = store.CaptureSave();
        store.SetSource("newer YAML", null);
        store.MarkSaved(new(response.Value with { Revision = 1 }, "new-etag"), "canonical first YAML", snapshot);
        Assert.AreEqual("newer YAML", store.State.SourceText);
        Assert.IsTrue(store.State.HasSourceChanges);
        Assert.IsTrue(store.State.IsDirty);
    }

    [TestMethod]
    public void ReloadingSameResourceInvalidatesOldAcknowledgements()
    {
        var response = SavingDraft();
        var store = new FlowEditorStore();
        store.Load(response, "old session");
        var snapshot = store.CaptureSave();
        store.Load(response, "new session");
        Assert.IsFalse(store.AcknowledgeSave(new(response.Value, "old-etag"), snapshot));
        Assert.IsFalse(store.MarkSaved(new(response.Value, "old-etag"), "old source", snapshot));
        Assert.AreEqual("new session", store.State.SourceText);
        Assert.AreEqual(response.ETag, store.State.ETag);
        Assert.IsFalse(store.State.IsDirty);
    }

    private static FlowDraftResponse SavingDraft() => new(new FlowDraft
    {
        WorkspaceId = WorkspaceId,
        ScopeRef = ResourceScopeRef.Workspace(WorkspaceId.Value),
        Id = "saving-draft",
        FlowId = new("saving"),
        DisplayName = "Saving",
        Definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] },
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch
    }, "initial-etag");

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
                new OutputFlowStepDefinition { Name = "failure", Outcome = FlowOutputOutcome.Error },
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
    public void ReconcileFlowCallTransitionsAlignsInvalidErrorRoutesWithDeclaredOutput()
    {
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("nested") },
                new OutputFlowStepDefinition { Name = "done" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("old-success", "child", "completed", "done"),
                new("current-success", "child", "done", "done"),
                new("arbitrary-obsolete-id", "child", "obsolete-error", "error"),
                new("child-failed-error", "child", "failed", "error")
            ]
        };
        var outputs = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal)
        {
            ["child"] = [new("done", "Done", FlowOutputOutcome.Success, null), new("failed", "Failed", FlowOutputOutcome.Error, null)]
        };

        var reconciled = new ReconcileFlowCallTransitionsCommand(outputs).Apply(definition);

        Assert.HasCount(2, reconciled.Transitions);
        CollectionAssert.AreEquivalent(new[] { "done", "failed" }, reconciled.Transitions.Select(item => item.Event).ToArray());

        var replaced = new ReconcileFlowCallTransitionsCommand(outputs).Apply(definition with
        {
            Transitions = [new("arbitrary-obsolete-id", "child", "obsolete-error", "error")]
        });
        Assert.AreEqual("failed", replaced.Transitions.Single().Event);
        Assert.AreEqual("arbitrary-obsolete-id", replaced.Transitions.Single().Id);

        var removed = new ReconcileFlowCallTransitionsCommand(new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal)
        {
            ["child"] = [new("done", "Done", FlowOutputOutcome.Success, null)]
        }).Apply(definition with
        {
            Transitions = [new("obsolete-error", "child", "obsolete-error", "error")]
        });
        Assert.HasCount(0, removed.Transitions);
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

        store.SetSource("entryStep: child # applied YAML");
        store.MarkSaved(response, "entryStep: child", store.CaptureSave());

        var node = store.State.Diagram.Nodes.Single(item => item.Name == "child");
        CollectionAssert.AreEqual(new[] { "approved", "rejected" }, node.OutputEvents.ToArray());
        Assert.AreEqual(FlowOutputOutcome.Error, node.OutputOutcomes["rejected"]);
    }

    [TestMethod]
    public void OwnerScopeSurvivesSavedStateReprojection()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var ownerScope = ResourceScopeRef.Tenant(Guid.Parse("4f8bdc56-42b8-4fdf-b0bf-79cba28a2197"));
        var draft = new FlowDraft
        {
            WorkspaceId = WorkspaceId,
            ScopeRef = ownerScope,
            Id = "editor-draft",
            FlowId = new("editor"),
            DisplayName = "Editor",
            Definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }] },
            CreatedAt = now,
            UpdatedAt = now
        };
        var response = new FlowDraftResponse(draft, "\"etag-1\"");
        var store = new FlowEditorStore();
        store.Load(response, "entryStep: input");

        store.SetSource("entryStep: input # applied YAML");
        store.MarkSaved(response, "entryStep: input", store.CaptureSave());

        Assert.AreEqual(ownerScope, store.State.Resource!.ScopeRef);
    }

    [TestMethod]
    public void ReapplyingEquivalentFlowCallOutputsDoesNotNotifyOrReproject()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps = [new FlowCallStepDefinition { Name = "child", Flow = new("nested") }]
        };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: child");
        var notifications = 0;
        store.StateChanged += (_, _) => notifications++;
        FlowDesignerOutput[] outputs = [new("approved", "Approved", FlowOutputOutcome.Success, null)];

        Assert.IsTrue(store.SetFlowCallOutputs("child", outputs));
        Assert.IsFalse(store.SetFlowCallOutputs("child", [new("approved", "Approved", FlowOutputOutcome.Success, null)]));
        Assert.AreEqual(1, notifications);
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
