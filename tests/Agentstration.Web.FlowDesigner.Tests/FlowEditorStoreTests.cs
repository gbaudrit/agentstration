using Agentstration.Flow;
using Agentstration.Flow.Contracts;
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
        var draft = new FlowDraft { Id = "editor-draft", FlowId = new("editor"), DisplayName = "Editor", Definition = definition, CreatedAt = now, UpdatedAt = now };
        var store = new FlowEditorStore();
        store.Load(new FlowDraftResponse(draft, "\"etag-1\""), "entryStep: input");

        await store.DispatchAsync(new AddStepCommand(new TransformFlowStepDefinition { Name = "transform" }, new(100, 200)));
        await store.DispatchAsync(new MoveStepCommand("transform", new(240, 320)));

        Assert.IsTrue(store.State.IsDirty);
        Assert.AreEqual(3, store.State.Diagram.Nodes.Count);
        Assert.AreEqual(new FlowNodePosition(240, 320), store.State.Draft!.Definition.Designer.NodePositions["transform"]);
        store.Undo();
        Assert.AreEqual(new FlowNodePosition(100, 200), store.State.Draft.Definition.Designer.NodePositions["transform"]);
        store.Undo();
        Assert.IsFalse(store.State.Draft.Definition.Steps.Any(step => step.Name == "transform"));
        store.Redo();
        Assert.IsTrue(store.State.Draft.Definition.Steps.Any(step => step.Name == "transform"));
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
                new("agent-output", "agent", "completed", "output"),
                new("agent-failure", "agent", "failed", "failure")
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
}
