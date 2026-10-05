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
            Steps = [new InputFlowStepDefinition { Name = "input" }, new AgentFlowStepDefinition { Name = "agent", Agent = new("sample") }],
            Transitions = [new("input-agent", "input", "completed", "agent")]
        };
        var draft = new FlowDraft { WorkspaceId = WorkspaceId, Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new AddStepCommand(
            new FailureFlowStepDefinition { Name = "failure" },
            new(300, 200),
            new("agent-error-failure", "agent", "error", "failure")));

        Assert.IsTrue(store.State.Resource!.Definition.Steps.Any(step => step.Name == "failure"));
        Assert.IsTrue(store.State.Resource.Definition.Transitions.Any(transition => transition.Id == "agent-error-failure"));
        store.Undo();
        Assert.IsFalse(store.State.Resource.Definition.Steps.Any(step => step.Name == "failure"));
        Assert.IsFalse(store.State.Resource.Definition.Transitions.Any(transition => transition.Id == "agent-error-failure"));
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
