using Agentstration.Flow;
using Agentstration.Flow.Contracts;

namespace Agentstration.Web.FlowDesigner.State;

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
        var positions = FlowGraphAutoLayout.Arrange(definition, Vertical);
        return definition with { Designer = definition.Designer with { NodePositions = positions, PreferredLayout = Vertical ? "Vertical" : "Horizontal" } };
    }
}

internal static class FlowGraphAutoLayout
{
    private const double PrimarySpacing = 320;
    private const double SecondarySpacing = 210;
    private const double Margin = 100;

    public static IReadOnlyDictionary<string, FlowNodePosition> Arrange(FlowGraphDefinition definition, bool vertical)
    {
        if (definition.Steps.Count == 0)
            return new Dictionary<string, FlowNodePosition>(StringComparer.Ordinal);
        var stepOrder = definition.Steps.Select((step, index) => (step.Name, index)).ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        var ranks = Rank(definition, stepOrder);
        var layers = definition.Steps
            .GroupBy(step => ranks[step.Name])
            .OrderBy(layer => layer.Key)
            .ToArray();
        var largestLayer = layers.Max(layer => layer.Count());
        var positions = new Dictionary<string, FlowNodePosition>(StringComparer.Ordinal);

        foreach (var layer in layers)
        {
            var nodes = layer.OrderBy(step => stepOrder[step.Name]).ToArray();
            var offset = (largestLayer - nodes.Length) * SecondarySpacing / 2;
            for (var index = 0; index < nodes.Length; index++)
            {
                var primary = Margin + layer.Key * PrimarySpacing;
                var secondary = Margin + offset + index * SecondarySpacing;
                positions[nodes[index].Name] = vertical ? new(secondary, primary) : new(primary, secondary);
            }
        }

        return positions;
    }

    private static IReadOnlyDictionary<string, int> Rank(FlowGraphDefinition definition, IReadOnlyDictionary<string, int> stepOrder)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var entry = stepOrder.ContainsKey(definition.EntryStep) ? definition.EntryStep : definition.Steps[0].Name;
        var queue = new Queue<string>();
        ranks[entry] = 0;
        queue.Enqueue(entry);

        while (queue.TryDequeue(out var current))
        {
            foreach (var target in definition.Transitions
                .Where(transition => transition.FromStep == current && stepOrder.ContainsKey(transition.ToStep))
                .Select(transition => transition.ToStep)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => stepOrder[name]))
            {
                if (ranks.ContainsKey(target)) continue;
                ranks[target] = ranks[current] + 1;
                queue.Enqueue(target);
            }
        }

        // Promote convergence nodes after their most distant predecessor. The
        // bounded relaxation remains safe for an invalid cyclic draft while
        // producing the expected longest-path layers for a valid DAG.
        for (var pass = 0; pass < definition.Steps.Count - 1; pass++)
        {
            var changed = false;
            foreach (var transition in definition.Transitions.Where(transition =>
                transition.ToStep != entry && ranks.ContainsKey(transition.FromStep) && stepOrder.ContainsKey(transition.ToStep)))
            {
                var candidate = Math.Min(definition.Steps.Count - 1, ranks[transition.FromStep] + 1);
                if (ranks.TryGetValue(transition.ToStep, out var currentRank) && currentRank >= candidate) continue;
                ranks[transition.ToStep] = candidate;
                changed = true;
            }
            if (!changed) break;
        }

        var nextRank = ranks.Count == 0 ? 0 : ranks.Values.Max() + 1;
        foreach (var step in definition.Steps.Where(step => !ranks.ContainsKey(step.Name)))
            ranks[step.Name] = nextRank++;
        return ranks;
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
