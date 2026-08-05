using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Web.Features.Flows.Designer;

namespace Agentstration.Web.Tests;

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
}
