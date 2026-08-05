using Agentstration.Flow;
using Agentstration.Flow.Contracts;

namespace Agentstration.Web.Features.Flows.Designer;

public enum FlowEditorMode { Designer, Definition, Split }
public enum FlowSaveState { Saved, Saving, UnsavedChanges, SaveFailed }
public sealed record FlowEditorSelection(string? StepName = null, string? TransitionId = null);
public sealed record FlowDesignerNode(string Name, string Type, string DisplayName, FlowNodePosition Position, string? Resource);
public sealed record FlowDesignerLink(string Id, string From, string To, string Event);
public sealed record FlowDesignerDocument(IReadOnlyList<FlowDesignerNode> Nodes, IReadOnlyList<FlowDesignerLink> Links)
{
    public static FlowDesignerDocument From(FlowGraphDefinition definition)
    {
        var nodes = definition.Steps.Select((step, index) => new FlowDesignerNode(step.Name, step.Type(), step.DisplayName ?? step.Name,
            definition.Designer.NodePositions.TryGetValue(step.Name, out var position) ? position : new(index * 200, 50),
            step switch { AgentFlowStepDefinition agent => agent.Agent.ResourceId, RouterFlowStepDefinition router => $"{router.Candidates.Count} routes", _ => null })).ToArray();
        return new(nodes, definition.Transitions.Select(transition => new FlowDesignerLink(transition.Id, transition.FromStep, transition.ToStep, transition.Event)).ToArray());
    }
}

public sealed record FlowEditorState
{
    public FlowDraft? Draft { get; init; }
    public FlowDesignerDocument Diagram { get; init; } = new([], []);
    public FlowEditorSelection Selection { get; init; } = new();
    public IReadOnlyList<FlowValidationIssue> Issues { get; init; } = [];
    public FlowEditorMode Mode { get; init; }
    public FlowSaveState SaveState { get; init; } = FlowSaveState.Saved;
    public bool IsDirty { get; init; }
    public long LocalRevision { get; init; }
    public string? ETag { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string? SourceError { get; init; }
}

public interface IFlowEditorCommand { FlowGraphDefinition Apply(FlowGraphDefinition definition); }
public sealed record ReplaceDefinitionCommand(FlowGraphDefinition Definition) : IFlowEditorCommand { public FlowGraphDefinition Apply(FlowGraphDefinition definition) => Definition; }
public sealed record AddStepCommand(FlowStepDefinition Step, FlowNodePosition Position) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Steps = [.. definition.Steps, Step], Designer = definition.Designer with { NodePositions = new Dictionary<string, FlowNodePosition>(definition.Designer.NodePositions, StringComparer.Ordinal) { [Step.Name] = Position } } };
}
public sealed record RemoveStepCommand(string Name) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition)
    {
        var positions = new Dictionary<string, FlowNodePosition>(definition.Designer.NodePositions, StringComparer.Ordinal); positions.Remove(Name);
        return definition with { Steps = definition.Steps.Where(step => step.Name != Name).ToArray(), Transitions = definition.Transitions.Where(transition => transition.FromStep != Name && transition.ToStep != Name).ToArray(), Designer = definition.Designer with { NodePositions = positions } };
    }
}
public sealed record MoveStepCommand(string Name, FlowNodePosition Position) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Designer = definition.Designer with { NodePositions = new Dictionary<string, FlowNodePosition>(definition.Designer.NodePositions, StringComparer.Ordinal) { [Name] = Position } } };
}
public sealed record UpdateStepCommand(FlowStepDefinition Step) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Steps = definition.Steps.Select(current => current.Name == Step.Name ? Step : current).ToArray() };
}
public sealed record AddTransitionCommand(FlowTransitionDefinition Transition) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Transitions = [.. definition.Transitions.Where(item => item.Id != Transition.Id), Transition] };
}
public sealed record RemoveTransitionCommand(string Id) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Transitions = definition.Transitions.Where(item => item.Id != Id).ToArray() };
}
public sealed record ApplyAutoLayoutCommand(bool Vertical) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition)
    {
        var positions = definition.Steps.Select((step, index) => new KeyValuePair<string, FlowNodePosition>(step.Name, Vertical ? new(120, 40 + index * 150) : new(40 + index * 210, 100))).ToDictionary();
        return definition with { Designer = definition.Designer with { NodePositions = positions, PreferredLayout = Vertical ? "Vertical" : "Horizontal" } };
    }
}

public sealed class FlowEditorStore
{
    private readonly Stack<FlowGraphDefinition> undo = new();
    private readonly Stack<FlowGraphDefinition> redo = new();
    public FlowEditorState State { get; private set; } = new();
    public event EventHandler? StateChanged;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Load(FlowDraftResponse response, string source)
    {
        undo.Clear(); redo.Clear();
        State = new FlowEditorState { Draft = response.Value, Diagram = FlowDesignerDocument.From(response.Value.Definition), ETag = response.ETag, LocalRevision = response.Value.Revision, SourceText = source, SaveState = FlowSaveState.Saved };
        Changed();
    }

    public Task DispatchAsync(IFlowEditorCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = State.Draft ?? throw new InvalidOperationException("The editor has not loaded a Draft.");
        undo.Push(draft.Definition); redo.Clear();
        var definition = command.Apply(draft.Definition);
        State = State with { Draft = draft with { Definition = definition }, Diagram = FlowDesignerDocument.From(definition), IsDirty = true, SaveState = FlowSaveState.UnsavedChanges, LocalRevision = State.LocalRevision + 1, SourceError = null };
        Changed(); return Task.CompletedTask;
    }

    public void SelectStep(string? name) { State = State with { Selection = new FlowEditorSelection(name) }; Changed(); }
    public void SelectTransition(string? id) { State = State with { Selection = new FlowEditorSelection(null, id) }; Changed(); }
    public void SetMode(FlowEditorMode mode) { State = State with { Mode = mode }; Changed(); }
    public void SetIssues(IReadOnlyList<FlowValidationIssue> issues) { State = State with { Issues = issues }; Changed(); }
    public void SetSource(string source, string? error = null) { State = State with { SourceText = source, SourceError = error, IsDirty = error is null || State.IsDirty }; Changed(); }
    public void MarkSaving() { State = State with { SaveState = FlowSaveState.Saving }; Changed(); }
    public void MarkSaveFailed() { State = State with { SaveState = FlowSaveState.SaveFailed }; Changed(); }
    public void MarkSaved(FlowDraftResponse response, string source) { State = State with { Draft = response.Value, Diagram = FlowDesignerDocument.From(response.Value.Definition), ETag = response.ETag, LocalRevision = response.Value.Revision, SourceText = source, IsDirty = false, SaveState = FlowSaveState.Saved, SourceError = null }; Changed(); }
    public void Undo() { if (!undo.TryPop(out var definition) || State.Draft is null) return; redo.Push(State.Draft.Definition); ReplaceHistory(definition); }
    public void Redo() { if (!redo.TryPop(out var definition) || State.Draft is null) return; undo.Push(State.Draft.Definition); ReplaceHistory(definition); }
    private void ReplaceHistory(FlowGraphDefinition definition) { State = State with { Draft = State.Draft! with { Definition = definition }, Diagram = FlowDesignerDocument.From(definition), IsDirty = true, SaveState = FlowSaveState.UnsavedChanges }; Changed(); }
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
}
