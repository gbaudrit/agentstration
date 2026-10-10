using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using System.Text.Json;

namespace Agentstration.Web.FlowDesigner.State;

public enum FlowEditorMode { Designer, Definition, Split }
public enum FlowSaveState { Saved, Saving, UnsavedChanges, SaveFailed }
public sealed record FlowEditorSelection(string? StepName = null, string? TransitionId = null);
public sealed record FlowDesignerNode(string Name, string Type, string DisplayName, FlowNodePosition Position, string? Resource, IReadOnlyList<string> OutputEvents)
{
    public IReadOnlyDictionary<string, FlowOutputOutcome> OutputOutcomes { get; init; } = new Dictionary<string, FlowOutputOutcome>(StringComparer.Ordinal);
}
public sealed record FlowDesignerLink(string Id, string From, string To, string Event);
public sealed record FlowDesignerDocument(IReadOnlyList<FlowDesignerNode> Nodes, IReadOnlyList<FlowDesignerLink> Links)
{
    public static FlowDesignerDocument From(FlowGraphDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerOutput>>? flowCallOutputs = null)
    {
        var nodes = definition.Steps.Select((step, index) =>
        {
            var resolvedOutputs = step is FlowCallStepDefinition && flowCallOutputs?.TryGetValue(step.Name, out var outputs) == true ? outputs : null;
            return new FlowDesignerNode(step.Name, step.Type(), step.DisplayName ?? step.Name,
                definition.Designer.NodePositions.TryGetValue(step.Name, out var position) ? position : new(index * 200, 50),
                step switch { AgentFlowStepDefinition agent => agent.Agent.ResourceId, RouterFlowStepDefinition router => $"{router.Candidates.Count} routes", FlowCallStepDefinition flow => flow.Flow.ResourceId, RepeatFlowStepDefinition repeat => $"{repeat.Flow.ResourceId} · ≤ {repeat.MaximumIterations}", ToolFlowStepDefinition tool => tool.Tool.ResourceId, ToolRouteFlowStepDefinition route => route.ToolSet.ResourceId, _ => null },
                resolvedOutputs?.Select(output => output.Name).ToArray()
                    ?? (step is FlowCallStepDefinition
                        ? []
                        : step.OutputEvents()))
            {
                OutputOutcomes = resolvedOutputs?.ToDictionary(output => output.Name, output => output.Outcome, StringComparer.Ordinal)
                    ?? new Dictionary<string, FlowOutputOutcome>(StringComparer.Ordinal)
            };
        }).ToArray();
        return new(nodes, definition.Transitions.Select(transition => new FlowDesignerLink(transition.Id, transition.FromStep, transition.ToStep, transition.Event)).ToArray());
    }
}

public sealed record FlowEditorState
{
    public FlowDesignerResource? Resource { get; init; }
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
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string? PublishedVersion { get; init; }
    public bool IsReadOnly { get; init; }
}

public interface IFlowEditorCommand { FlowGraphDefinition Apply(FlowGraphDefinition definition); }
public sealed record ReplaceDefinitionCommand(FlowGraphDefinition Definition) : IFlowEditorCommand { public FlowGraphDefinition Apply(FlowGraphDefinition definition) => Definition; }
public sealed record AddStepCommand(
    FlowStepDefinition Step,
    FlowNodePosition Position,
    FlowTransitionDefinition? Transition = null) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with
    {
        Steps = [.. definition.Steps, Step],
        Transitions = Transition is null ? definition.Transitions : [.. definition.Transitions, Transition],
        Designer = definition.Designer with
        {
            NodePositions = new Dictionary<string, FlowNodePosition>(definition.Designer.NodePositions, StringComparer.Ordinal)
            {
                [Step.Name] = Position
            }
        }
    };
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
public sealed record RenameStepCommand(string OldName, string NewName) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(NewName) || definition.Steps.Any(step => step.Name == NewName && step.Name != OldName)) return definition;
        var steps = definition.Steps.Select(step => step.Name == OldName ? step with { Name = NewName } : step).ToArray();
        var transitions = definition.Transitions.Select(item => item with
        {
            FromStep = item.FromStep == OldName ? NewName : item.FromStep,
            ToStep = item.ToStep == OldName ? NewName : item.ToStep
        }).ToArray();
        var positions = new Dictionary<string, FlowNodePosition>(definition.Designer.NodePositions, StringComparer.Ordinal);
        if (positions.Remove(OldName, out var position)) positions[NewName] = position;
        return definition with { EntryStep = definition.EntryStep == OldName ? NewName : definition.EntryStep, Steps = steps, Transitions = transitions, Designer = definition.Designer with { NodePositions = positions } };
    }
}
public sealed record AddTransitionCommand(FlowTransitionDefinition Transition) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Transitions = [.. definition.Transitions.Where(item => item.Id != Transition.Id), Transition] };
}
public sealed record UpdateTransitionCommand(FlowTransitionDefinition Transition) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition)
    {
        if (definition.Transitions.All(item => item.Id != Transition.Id))
            return definition;

        return definition with
        {
            Transitions = definition.Transitions
                .Select(item => item.Id == Transition.Id ? Transition : item)
                .ToArray()
        };
    }
}
public sealed record ReconnectTransitionCommand(string Id, string FromStep, string ToStep, string Event) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with
    {
        Transitions = definition.Transitions
            .Select(item => item.Id == Id ? item with { FromStep = FromStep, ToStep = ToStep, Event = Event } : item)
            .ToArray()
    };
}
public sealed record RemoveTransitionCommand(string Id) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition) => definition with { Transitions = definition.Transitions.Where(item => item.Id != Id).ToArray() };
}
public sealed record ReconcileFlowCallTransitionsCommand(
    IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerOutput>> OutputsByStep) : IFlowEditorCommand
{
    public FlowGraphDefinition Apply(FlowGraphDefinition definition)
    {
        var transitions = definition.Transitions.ToList();
        var changed = false;
        var localOutputs = definition.Steps
            .Select(step => step switch
            {
                FailureFlowStepDefinition => new KeyValuePair<string, FlowOutputOutcome>(step.Name, FlowOutputOutcome.Error),
                OutputFlowStepDefinition output => new(step.Name, output.Outcome ?? FlowOutputOutcome.Success),
                _ => (KeyValuePair<string, FlowOutputOutcome>?)null
            })
            .OfType<KeyValuePair<string, FlowOutputOutcome>>()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var call in definition.Steps.OfType<FlowCallStepDefinition>())
        {
            if (!OutputsByStep.TryGetValue(call.Name, out var outputs) || outputs.Count == 0)
                continue;

            var declaredEvents = outputs.Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var invalid in transitions.Where(transition =>
                transition.FromStep == call.Name
                && !declaredEvents.Contains(transition.Event)
                && localOutputs.ContainsKey(transition.ToStep)
                && transition.Condition is null
                && transition.Priority is null).ToArray())
            {
                var candidates = outputs
                    .Where(output => output.Outcome == localOutputs[invalid.ToStep])
                    .Select(output => output.Name)
                    .ToArray();
                var replacement = candidates.Length == 1 ? candidates[0] : null;
                if (replacement is null) continue;

                var index = transitions.IndexOf(invalid);
                if (transitions.Any(transition =>
                    transition.Id != invalid.Id
                    && transition.FromStep == call.Name
                    && transition.ToStep == invalid.ToStep
                    && transition.Event == replacement))
                    transitions.RemoveAt(index);
                else
                    transitions[index] = invalid with { Event = replacement };
                changed = true;
            }
        }

        return changed ? definition with { Transitions = transitions } : definition;
    }
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
    private IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerOutput>> flowCallOutputs = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal);
    public event EventHandler? StateChanged;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Load(FlowDesignerLoadResult result, FlowDesignerTarget target)
    {
        undo.Clear(); redo.Clear();
        flowCallOutputs = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(StringComparer.Ordinal);
        State = new FlowEditorState { Resource = result.Resource, Diagram = FlowDesignerDocument.From(result.Resource.Definition), ETag = result.ETag, LocalRevision = result.Resource.DraftRevision ?? 0, SourceText = result.Source, SaveState = FlowSaveState.Saved, Namespace = target.Namespace, PublishedVersion = result.PublishedVersion, IsReadOnly = result.IsReadOnly };
        Changed();
    }

    public void Load(FlowDraftResponse response, string source) => Load(FlowDesignerLoadResult.FromDraft(response, source), new FlowDesignerTarget(ResourceNamespace.Default, response.Value.FlowId.Value));

    public Task DispatchAsync(IFlowEditorCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State.IsReadOnly) throw new InvalidOperationException("A published namespaced Flow is read-only.");
        var resource = State.Resource ?? throw new InvalidOperationException("The editor has not loaded a Flow definition.");
        var definition = command.Apply(resource.Definition);
        if (ReferenceEquals(definition, resource.Definition)) return Task.CompletedTask;
        undo.Push(resource.Definition); redo.Clear();
        var selection = command is RenameStepCommand rename && State.Selection.StepName == rename.OldName
            ? new FlowEditorSelection(rename.NewName)
            : State.Selection;
        State = State with { Resource = resource with { Definition = definition }, Diagram = FlowDesignerDocument.From(definition, flowCallOutputs), Selection = selection, IsDirty = true, SaveState = FlowSaveState.UnsavedChanges, LocalRevision = State.LocalRevision + 1, SourceError = null };
        Changed(); return Task.CompletedTask;
    }

    public void SelectStep(string? name) { State = State with { Selection = new FlowEditorSelection(name) }; Changed(); }
    public void SelectTransition(string? id) { State = State with { Selection = new FlowEditorSelection(null, id) }; Changed(); }
    public void SetMode(FlowEditorMode mode) { State = State with { Mode = mode }; Changed(); }
    public void SetIssues(IReadOnlyList<FlowValidationIssue> issues) { State = State with { Issues = issues }; Changed(); }
    public bool SetFlowCallOutputs(string stepName, IReadOnlyList<FlowDesignerOutput> outputs)
    {
        if ((flowCallOutputs.TryGetValue(stepName, out var current) && OutputsEqual(current, outputs)) ||
            (!flowCallOutputs.ContainsKey(stepName) && outputs.Count == 0))
            return false;
        var updated = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(flowCallOutputs, StringComparer.Ordinal);
        if (outputs.Count == 0) updated.Remove(stepName); else updated[stepName] = outputs;
        flowCallOutputs = updated;
        if (State.Resource is not null)
            State = State with { Diagram = FlowDesignerDocument.From(State.Resource.Definition, flowCallOutputs) };
        Changed();
        return true;
    }
    public bool SetFlowCallOutputs(IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerOutput>> outputs)
    {
        if (flowCallOutputs.Count == outputs.Count && outputs.All(pair =>
                flowCallOutputs.TryGetValue(pair.Key, out var current) && OutputsEqual(current, pair.Value)))
            return false;
        flowCallOutputs = new Dictionary<string, IReadOnlyList<FlowDesignerOutput>>(outputs, StringComparer.Ordinal);
        if (State.Resource is not null)
            State = State with { Diagram = FlowDesignerDocument.From(State.Resource.Definition, flowCallOutputs) };
        Changed();
        return true;
    }
    private static bool OutputsEqual(IReadOnlyList<FlowDesignerOutput> left, IReadOnlyList<FlowDesignerOutput> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name &&
            pair.First.DisplayName == pair.Second.DisplayName &&
            pair.First.Outcome == pair.Second.Outcome &&
            SchemaText(pair.First.Schema) == SchemaText(pair.Second.Schema));
    private static string? SchemaText(JsonElement? schema) => schema?.GetRawText();
    public void SetSource(string source, string? error = null) { State = State with { SourceText = source, SourceError = error, IsDirty = error is null || State.IsDirty }; Changed(); }
    public void MarkSaving() { State = State with { SaveState = FlowSaveState.Saving }; Changed(); }
    public void MarkSaveFailed() { State = State with { SaveState = FlowSaveState.SaveFailed }; Changed(); }
    public void MarkSaved(FlowDraftResponse response, string source) { State = State with { Resource = new(response.Value.FlowId, response.Value.DisplayName, response.Value.Description, response.Value.Tags, response.Value.Definition, response.Value.Revision), Diagram = FlowDesignerDocument.From(response.Value.Definition, flowCallOutputs), ETag = response.ETag, LocalRevision = response.Value.Revision, SourceText = source, IsDirty = false, SaveState = FlowSaveState.Saved, SourceError = null }; Changed(); }
    public void Undo() { if (!undo.TryPop(out var definition) || State.Resource is null || State.IsReadOnly) return; redo.Push(State.Resource.Definition); ReplaceHistory(definition); }
    public void Redo() { if (!redo.TryPop(out var definition) || State.Resource is null || State.IsReadOnly) return; undo.Push(State.Resource.Definition); ReplaceHistory(definition); }
    private void ReplaceHistory(FlowGraphDefinition definition) { State = State with { Resource = State.Resource! with { Definition = definition }, Diagram = FlowDesignerDocument.From(definition, flowCallOutputs), IsDirty = true, SaveState = FlowSaveState.UnsavedChanges }; Changed(); }
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
}
