using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.State;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowDesignerSessionFoundationTests
{
    [TestMethod]
    public async Task AcceptedTargetReplacementClearsPrivateGraphSourceHistoryAndOldSaveAcknowledgements()
    {
        var workspace = WorkspaceId.New();
        var draft = new FlowDraftResponse(new FlowDraft
        {
            WorkspaceId = workspace, ScopeRef = ResourceScopeRef.Workspace(workspace.Value), Id = "private-draft", FlowId = new("private-flow"),
            DisplayName = "Private saved title", Definition = new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] },
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        }, "original-etag");
        var store = new FlowEditorStore(); store.Load(draft, "private saved source");
        await store.DispatchAsync(new MoveStepCommand("input", new(10, 20)));
        store.SetSource("private invalid YAML [", "private parser error");
        var sent = store.CaptureSave(); Assert.IsTrue(store.CanUndo);

        store.Clear();

        Assert.IsNull(store.State.Resource); Assert.AreEqual(string.Empty, store.State.SourceText); Assert.IsNull(store.State.SourceError);
        Assert.IsEmpty(store.State.Diagram.Nodes); Assert.IsEmpty(store.State.Issues); Assert.IsNull(store.State.ETag);
        Assert.IsFalse(store.State.IsDirty); Assert.IsFalse(store.State.HasSourceChanges); Assert.IsFalse(store.CanUndo); Assert.IsFalse(store.CanRedo);
        Assert.IsFalse(store.IsCurrent(sent)); Assert.IsFalse(store.AcknowledgeSave(new(draft.Value, "late-etag"), sent));
        Assert.IsFalse(store.MarkSaved(new(draft.Value, "late-etag"), "late private source", sent));
        Assert.IsNull(store.State.Resource); Assert.AreEqual(string.Empty, store.State.SourceText);
    }
}
