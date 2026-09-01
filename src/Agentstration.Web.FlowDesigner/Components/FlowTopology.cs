using Agentstration.Flow;

namespace Agentstration.Web.FlowDesigner.Components;

public enum FlowTopologyEdgeKind
{
    Standard,
    Conditional,
    Fallback,
    Collaboration,
    Dynamic
}

public enum FlowTopologyEdgeState
{
    Declared,
    Observed,
    Inactive
}

public sealed record FlowTopologyNode(
    string Id,
    string SelectionKey,
    string Label,
    string Type,
    double X,
    double Y,
    string? Subtitle = null,
    string? Status = null,
    TimeSpan? Duration = null,
    int TurnCount = 0,
    bool IsCoordinator = false,
    bool IsInitial = false);

public sealed record FlowTopologyEdge(
    string Id,
    string From,
    string To,
    string? Label = null,
    FlowTopologyEdgeKind Kind = FlowTopologyEdgeKind.Standard,
    FlowTopologyEdgeState State = FlowTopologyEdgeState.Declared,
    bool Bidirectional = false);

public sealed record FlowTopologyTransfer(int Order, string From, string To, long Sequence);

public sealed record FlowTopologyGraph(
    IReadOnlyList<FlowTopologyNode> Nodes,
    IReadOnlyList<FlowTopologyEdge> Edges,
    string Layout,
    string Description)
{
    public IReadOnlyList<FlowTopologyTransfer> Transfers { get; init; } = [];

    public FlowTopologyNode? FindBySelection(string? selectionKey) =>
        selectionKey is null ? null : Nodes.FirstOrDefault(node => node.SelectionKey == selectionKey);
}

public static class FlowTopologyProjector
{
    private const double HorizontalGap = 300;
    private const double VerticalGap = 170;

    public static FlowTopologyGraph Project(FlowDefinition definition, FlowGraphDefinition? graph = null) =>
        ProjectCore(definition, graph, null, []);

    public static FlowTopologyGraph Project(FlowRun run, IReadOnlyList<FlowRunEvent>? events = null) =>
        ProjectCore(run.DefinitionSnapshot.Definition, run.DefinitionSnapshot.Graph, run, events ?? []);

    private static FlowTopologyGraph ProjectCore(
        FlowDefinition definition,
        FlowGraphDefinition? graph,
        FlowRun? run,
        IReadOnlyList<FlowRunEvent> events)
    {
        if (graph is not null) return ProjectWorkflowGraph(graph, run);

        return definition switch
        {
            DirectFlowDefinition direct => ProjectDirect(direct, run),
            RoutingFlowDefinition routing => ProjectRouting(routing, run),
            WorkflowFlowDefinition workflow => ProjectWorkflow(workflow, run),
            OrchestrationFlowDefinition orchestration => ProjectOrchestration(orchestration, run, events),
            CompositeFlowDefinition composite => ProjectComposite(composite, run),
            _ => new([], [], "horizontal", "Unsupported Flow topology")
        };
    }

    private static FlowTopologyGraph ProjectDirect(DirectFlowDefinition definition, FlowRun? run)
    {
        var nodes = new[]
        {
            SystemNode("input", "Input", "input", 0, 0, run),
            TargetNode("agent", definition.Target.Id, "agent", HorizontalGap, 0, run, "Agent"),
            SystemNode("output", "Output", "output", HorizontalGap * 2, 0, run)
        };
        return new(nodes, LinearEdges(nodes), "horizontal", "Direct Flow");
    }

    private static FlowTopologyGraph ProjectRouting(RoutingFlowDefinition definition, FlowRun? run)
    {
        var candidates = definition.Destinations
            .Concat(definition.Fallback is null ? [] : [definition.Fallback])
            .DistinctBy(target => target.Id, StringComparer.Ordinal)
            .ToArray();
        var middle = Math.Max(0, (candidates.Length - 1) * VerticalGap / 2);
        var nodes = new List<FlowTopologyNode>
        {
            SystemNode("input", "Input", "input", 0, middle, run),
            SystemNode("router", "Router", "router", HorizontalGap, middle, run)
        };
        nodes.AddRange(candidates.Select((target, index) =>
            TargetNode($"candidate:{target.Id}", target.Id, "agent", HorizontalGap * 2, index * VerticalGap, run, "Candidate")));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * 3, middle, run));

        var selected = run?.Steps.FirstOrDefault(step => step.StepType == "agent")?.AgentResourceId;
        var edges = new List<FlowTopologyEdge> { new("input-router", "system:input", "system:router") };
        foreach (var target in candidates)
        {
            var isFallback = definition.Fallback?.Id == target.Id;
            var observed = selected is not null && selected.EndsWith(target.Id, StringComparison.Ordinal);
            edges.Add(new($"route:{target.Id}", "system:router", $"candidate:{target.Id}", isFallback ? "fallback" : target.Id,
                isFallback ? FlowTopologyEdgeKind.Fallback : FlowTopologyEdgeKind.Conditional,
                selected is null ? FlowTopologyEdgeState.Declared : observed ? FlowTopologyEdgeState.Observed : FlowTopologyEdgeState.Inactive));
            edges.Add(new($"output:{target.Id}", $"candidate:{target.Id}", "system:output", null,
                FlowTopologyEdgeKind.Standard,
                selected is null ? FlowTopologyEdgeState.Declared : observed ? FlowTopologyEdgeState.Observed : FlowTopologyEdgeState.Inactive));
        }
        return new(nodes, edges, "branching", "Routing Flow with candidate branches");
    }

    private static FlowTopologyGraph ProjectWorkflowGraph(FlowGraphDefinition graph, FlowRun? run)
    {
        var positions = graph.Steps.Select((step, index) =>
        {
            var position = graph.Designer.NodePositions.TryGetValue(step.Name, out var stored)
                ? stored
                : new FlowNodePosition(index * HorizontalGap, 0);
            return Node(step.Name, step.DisplayName ?? step.Name, step.Type(), position.X, position.Y, run, StepSubtitle(step));
        }).ToArray();
        var selectedTransitions = run?.Steps
            .Where(step => step.SelectedTransition is not null)
            .Select(step => step.SelectedTransition!)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var executedSources = run?.Steps
            .Where(step => step.Status is not FlowStepRunStatus.NotStarted and not FlowStepRunStatus.Skipped)
            .Select(step => step.StepName)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var edges = graph.Transitions.Select(transition => new FlowTopologyEdge(
            transition.Id,
            $"step:{transition.FromStep}",
            $"step:{transition.ToStep}",
            transition.Condition is null ? transition.Event : $"{transition.Event} · {transition.Condition}",
            transition.Event is "true" or "false" || transition.Condition is not null ? FlowTopologyEdgeKind.Conditional : FlowTopologyEdgeKind.Standard,
            run is null ? FlowTopologyEdgeState.Declared
                : selectedTransitions.Contains(transition.Id) ? FlowTopologyEdgeState.Observed
                : executedSources.Contains(transition.FromStep) ? FlowTopologyEdgeState.Inactive
                : FlowTopologyEdgeState.Declared)).ToArray();
        return new(positions, edges, graph.Designer.PreferredLayout ?? "custom", "Workflow graph with declared transitions");
    }

    private static FlowTopologyGraph ProjectWorkflow(WorkflowFlowDefinition definition, FlowRun? run)
    {
        var nodes = definition.Nodes.Select((node, index) => Node(node.Id, node.Id, node.Kind.ToString().ToLowerInvariant(), index * HorizontalGap, 0, run, node.Target?.Id)).ToArray();
        var edges = definition.Edges.Select((edge, index) => new FlowTopologyEdge(
            $"workflow:{index}", $"step:{edge.Source}", $"step:{edge.Destination}", edge.Condition,
            edge.Condition is null ? FlowTopologyEdgeKind.Standard : FlowTopologyEdgeKind.Conditional)).ToArray();
        return new(nodes, edges, "graph", "Workflow graph with declared edges");
    }

    private static FlowTopologyGraph ProjectOrchestration(
        OrchestrationFlowDefinition definition,
        FlowRun? run,
        IReadOnlyList<FlowRunEvent> events)
    {
        var turns = events
            .Where(item => item.Type == FlowRunEventType.ParticipantTurnStarted && item.StepId is not null)
            .GroupBy(item => item.StepId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return definition.Pattern switch
        {
            SequentialOrchestrationPattern => ProjectSequential(definition, run, turns),
            ConcurrentOrchestrationPattern => ProjectConcurrent(definition, run, turns),
            HandoffOrchestrationPattern handoff => ProjectHandoff(definition, handoff, run, events, turns),
            GroupChatOrchestrationPattern groupChat => ProjectGroupChat(definition, groupChat, run, events, turns),
            MagenticOrchestrationPattern magentic => ProjectMagentic(definition, magentic, run, turns),
            _ => new([], [], "orchestration", "Unsupported orchestration topology")
        };
    }

    private static FlowTopologyGraph ProjectSequential(OrchestrationFlowDefinition definition, FlowRun? run, IReadOnlyDictionary<string, int> turns)
    {
        var nodes = new List<FlowTopologyNode> { SystemNode("input", "Input", "input", 0, 0, run) };
        nodes.AddRange(definition.Participants.Select((participant, index) => ParticipantNode(participant, HorizontalGap * (index + 1), 0, run, turns)));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * (definition.Participants.Count + 1), 0, run));
        return new(nodes, LinearEdges(nodes), "horizontal", "Sequential orchestration in declaration order");
    }

    private static FlowTopologyGraph ProjectConcurrent(OrchestrationFlowDefinition definition, FlowRun? run, IReadOnlyDictionary<string, int> turns)
    {
        var middle = Math.Max(0, (definition.Participants.Count - 1) * VerticalGap / 2);
        var nodes = new List<FlowTopologyNode> { SystemNode("input", "Input", "input", 0, middle, run) };
        nodes.AddRange(definition.Participants.Select((participant, index) => ParticipantNode(participant, HorizontalGap, index * VerticalGap, run, turns)));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * 2, middle, run));
        var edges = definition.Participants.SelectMany(participant => new[]
        {
            new FlowTopologyEdge($"input:{participant.Id}", "system:input", $"participant:{participant.Id}"),
            new FlowTopologyEdge($"output:{participant.Id}", $"participant:{participant.Id}", "system:output")
        }).ToArray();
        return new(nodes, edges, "fan-out", "Concurrent orchestration with independent participants");
    }

    private static FlowTopologyGraph ProjectHandoff(
        OrchestrationFlowDefinition definition,
        HandoffOrchestrationPattern pattern,
        FlowRun? run,
        IReadOnlyList<FlowRunEvent> events,
        IReadOnlyDictionary<string, int> turns)
    {
        var participantOrder = definition.Participants
            .Select((participant, index) => (participant.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var ranks = HandoffRanks(definition, pattern, participantOrder);
        var rankGroups = definition.Participants
            .GroupBy(participant => ranks[participant.Id])
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(participant => participantOrder[participant.Id]).ToArray())
            .ToArray();
        var maximumRows = Math.Max(1, rankGroups.Select(group => group.Length).DefaultIfEmpty(1).Max());
        var centerY = (maximumRows - 1) * VerticalGap / 2;
        var nodes = new List<FlowTopologyNode> { SystemNode("input", "Input", "input", 0, centerY, run) };
        foreach (var group in rankGroups)
        {
            var rank = ranks[group[0].Id];
            var firstY = centerY - ((group.Length - 1) * VerticalGap / 2);
            nodes.AddRange(group.Select((participant, index) =>
                ParticipantNode(participant, HorizontalGap * (rank + 1), firstY + (index * VerticalGap), run, turns) with
                {
                    IsInitial = participant.Id == pattern.InitialParticipant
                }));
        }

        var maximumRank = ranks.Values.DefaultIfEmpty(0).Max();
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * (maximumRank + 2), centerY, run));

        var observedTransfers = ObservedParticipantTransfers(events);
        var observedOrders = observedTransfers
            .GroupBy(transfer => (transfer.From, transfer.To))
            .ToDictionary(group => group.Key, group => group.Select(transfer => transfer.Order).ToArray());
        var initialParticipantObserved = events.Any(item =>
                item.Type == FlowRunEventType.ParticipantTurnStarted
                && string.Equals(item.StepId, pattern.InitialParticipant, StringComparison.Ordinal))
            || observedTransfers.Any(transfer => string.Equals(transfer.From, pattern.InitialParticipant, StringComparison.Ordinal));
        var terminalParticipant = run?.Status == FlowRunStatus.Succeeded
            ? LastObservedParticipant(events, observedTransfers)
            : null;
        var edges = new List<FlowTopologyEdge>
        {
            new(
                "handoff-entry",
                "system:input",
                $"participant:{pattern.InitialParticipant}",
                "initial",
                State: initialParticipantObserved ? FlowTopologyEdgeState.Observed : FlowTopologyEdgeState.Declared)
        };
        edges.AddRange(pattern.Handoffs.Select((route, index) =>
        {
            var observed = observedOrders.TryGetValue((route.From, route.To), out var orders);
            var label = observed
                ? $"handoff · {string.Join(", ", orders!.Select(order => $"#{order}"))}"
                : "handoff";
            return new FlowTopologyEdge(
                $"handoff:{index}", $"participant:{route.From}", $"participant:{route.To}", label,
                FlowTopologyEdgeKind.Conditional,
                observed ? FlowTopologyEdgeState.Observed : FlowTopologyEdgeState.Declared);
        }));
        edges.AddRange(definition.Participants.Select(participant => new FlowTopologyEdge(
            $"terminal:{participant.Id}", $"participant:{participant.Id}", "system:output", "terminal",
            FlowTopologyEdgeKind.Dynamic,
            participant.Id == terminalParticipant ? FlowTopologyEdgeState.Observed : FlowTopologyEdgeState.Declared)));
        return new(nodes, edges, "directed", "Handoff orchestration with declared routes")
        {
            Transfers = observedTransfers
        };
    }

    private static IReadOnlyDictionary<string, int> HandoffRanks(
        OrchestrationFlowDefinition definition,
        HandoffOrchestrationPattern pattern,
        IReadOnlyDictionary<string, int> participantOrder)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (participantOrder.ContainsKey(pattern.InitialParticipant))
        {
            ranks[pattern.InitialParticipant] = 0;
            var queue = new Queue<string>();
            queue.Enqueue(pattern.InitialParticipant);
            while (queue.TryDequeue(out var source))
            {
                foreach (var target in pattern.Handoffs
                    .Where(route => route.From == source && participantOrder.ContainsKey(route.To))
                    .Select(route => route.To)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(target => participantOrder[target]))
                {
                    if (ranks.ContainsKey(target)) continue;
                    ranks[target] = ranks[source] + 1;
                    queue.Enqueue(target);
                }
            }
        }

        var fallbackRank = ranks.Values.DefaultIfEmpty(-1).Max() + 1;
        foreach (var participant in definition.Participants.Where(participant => !ranks.ContainsKey(participant.Id)))
        {
            ranks[participant.Id] = fallbackRank;
        }

        return ranks;
    }

    private static FlowTopologyGraph ProjectGroupChat(
        OrchestrationFlowDefinition definition,
        GroupChatOrchestrationPattern pattern,
        FlowRun? run,
        IReadOnlyList<FlowRunEvent> events,
        IReadOnlyDictionary<string, int> turns)
    {
        var middle = Math.Max(0, (definition.Participants.Count - 1) * VerticalGap / 2);
        var nodes = new List<FlowTopologyNode>
        {
            SystemNode("input", "Input", "input", 0, middle, run),
            new("coordinator:conversation", "Conversation", "Shared conversation", "conversation", HorizontalGap, middle, $"Maximum {pattern.MaximumIterations} iterations", IsCoordinator: true)
        };
        nodes.AddRange(definition.Participants.Select((participant, index) => ParticipantNode(participant, HorizontalGap * 2, index * VerticalGap, run, turns)));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * 3, middle, run));
        var edges = new List<FlowTopologyEdge>
        {
            new("conversation-entry", "system:input", "coordinator:conversation"),
            new("conversation-output", "coordinator:conversation", "system:output")
        };
        edges.AddRange(definition.Participants.Select(participant => new FlowTopologyEdge(
            $"conversation:{participant.Id}", "coordinator:conversation", $"participant:{participant.Id}", "shared context",
            FlowTopologyEdgeKind.Collaboration, Bidirectional: true)));
        edges.AddRange(ObservedParticipantPairs(events).Select((pair, index) => new FlowTopologyEdge(
            $"observed:{index}", $"participant:{pair.From}", $"participant:{pair.To}", "next turn",
            FlowTopologyEdgeKind.Dynamic, FlowTopologyEdgeState.Observed)));
        return new(nodes, edges, "hub", "Group Chat orchestration around a shared conversation");
    }

    private static FlowTopologyGraph ProjectMagentic(
        OrchestrationFlowDefinition definition,
        MagenticOrchestrationPattern pattern,
        FlowRun? run,
        IReadOnlyDictionary<string, int> turns)
    {
        var middle = Math.Max(0, (definition.Participants.Count - 1) * VerticalGap / 2);
        var nodes = new List<FlowTopologyNode>
        {
            SystemNode("input", "Input", "input", 0, middle, run),
            new("coordinator:manager", pattern.Manager.Id, pattern.Manager.Id, "manager", HorizontalGap, middle, $"Manager · max {pattern.MaximumRounds} rounds", IsCoordinator: true)
        };
        nodes.AddRange(definition.Participants.Select((participant, index) => ParticipantNode(participant, HorizontalGap * 2, index * VerticalGap, run, turns)));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * 3, middle, run));
        var edges = new List<FlowTopologyEdge>
        {
            new("manager-entry", "system:input", "coordinator:manager"),
            new("manager-output", "coordinator:manager", "system:output")
        };
        edges.AddRange(definition.Participants.Select(participant => new FlowTopologyEdge(
            $"manager:{participant.Id}", "coordinator:manager", $"participant:{participant.Id}", "delegate",
            FlowTopologyEdgeKind.Collaboration, Bidirectional: true)));
        return new(nodes, edges, "managed", "Magentic orchestration coordinated by a dedicated manager");
    }

    private static FlowTopologyGraph ProjectComposite(CompositeFlowDefinition definition, FlowRun? run)
    {
        var nodes = new List<FlowTopologyNode> { SystemNode("input", "Input", "input", 0, 0, run) };
        nodes.AddRange(definition.Flows.Select((flow, index) => new FlowTopologyNode(
            $"flow:{flow.FlowId.Value}", flow.FlowId.Value, flow.FlowId.Value, "flow", HorizontalGap * (index + 1), 0,
            flow.Version ?? "active version")));
        nodes.Add(SystemNode("output", "Output", "output", HorizontalGap * (definition.Flows.Count + 1), 0, run));
        return new(nodes, LinearEdges(nodes), definition.Mode.ToString().ToLowerInvariant(), $"Composite Flow ({definition.Mode}); execution is not implemented");
    }

    private static FlowTopologyNode SystemNode(string key, string label, string type, double x, double y, FlowRun? run) =>
        Node(label, label, type, x, y, run, null, "system", $"system:{key}");

    private static FlowTopologyNode TargetNode(string id, string label, string type, double x, double y, FlowRun? run, string? subtitle) =>
        Node(label, label, type, x, y, run, subtitle, id.StartsWith("candidate:", StringComparison.Ordinal) ? "candidate" : "target", id);

    private static FlowTopologyNode ParticipantNode(FlowTargetReference participant, double x, double y, FlowRun? run, IReadOnlyDictionary<string, int> turns) =>
        Node(participant.Id, participant.Id, "agent", x, y, run, "Participant", "participant", $"participant:{participant.Id}") with
        {
            TurnCount = turns.GetValueOrDefault(participant.Id)
        };

    private static FlowTopologyNode Node(
        string selectionKey,
        string label,
        string type,
        double x,
        double y,
        FlowRun? run,
        string? subtitle,
        string prefix = "step",
        string? explicitId = null)
    {
        var step = FindStep(run, selectionKey, type);
        return new(
            explicitId ?? $"{prefix}:{selectionKey}",
            selectionKey,
            label,
            type,
            x,
            y,
            subtitle,
            step?.Status.ToString(),
            step?.StartedAt is not null && step.CompletedAt is not null ? step.CompletedAt - step.StartedAt : null);
    }

    private static FlowStepRun? FindStep(FlowRun? run, string selectionKey, string type)
    {
        if (run is null) return null;
        var exact = run.Steps.FirstOrDefault(step => step.StepName == selectionKey);
        if (exact is not null) return exact;
        return type switch
        {
            "input" => run.Steps.FirstOrDefault(step => step.StepType == "input"),
            "output" => run.Steps.FirstOrDefault(step => step.StepType == "output"),
            "router" => run.Steps.FirstOrDefault(step => step.StepType == "router"),
            "agent" => run.Steps.FirstOrDefault(step => step.StepType == "agent" &&
                (step.AgentResourceId?.EndsWith(selectionKey, StringComparison.Ordinal) ?? false)),
            _ => null
        };
    }

    private static IReadOnlyList<FlowTopologyEdge> LinearEdges(IReadOnlyList<FlowTopologyNode> nodes) =>
        nodes.Zip(nodes.Skip(1), (from, to) => new FlowTopologyEdge($"{from.Id}:{to.Id}", from.Id, to.Id)).ToArray();

    private static string? StepSubtitle(FlowStepDefinition step) => step switch
    {
        AgentFlowStepDefinition agent => agent.Agent.ResourceId,
        RouterFlowStepDefinition router => $"{router.Candidates.Count} routes",
        ConditionFlowStepDefinition condition => condition.Mode,
        TransformFlowStepDefinition transform => transform.Mode,
        FailureFlowStepDefinition failure => failure.Code,
        _ => null
    };

    private static HashSet<(string From, string To)> ObservedParticipantPairs(IReadOnlyList<FlowRunEvent> events)
    {
        return ObservedParticipantTransfers(events)
            .Select(transfer => (transfer.From, transfer.To))
            .ToHashSet();
    }

    private static IReadOnlyList<FlowTopologyTransfer> ObservedParticipantTransfers(IReadOnlyList<FlowRunEvent> events)
    {
        var explicitTransfers = events
            .Where(item => item.Type == FlowRunEventType.ParticipantHandoff)
            .OrderBy(item => item.Sequence)
            .Select(item => new
            {
                From = PayloadString(item.Payload, "from") ?? item.StepId,
                To = PayloadString(item.Payload, "to"),
                item.Sequence
            })
            .Where(item => item.From is not null && item.To is not null)
            .Select((item, index) => new FlowTopologyTransfer(index + 1, item.From!, item.To!, item.Sequence))
            .ToArray();
        if (explicitTransfers.Length > 0) return explicitTransfers;

        var turns = events
            .Where(item => item.Type == FlowRunEventType.ParticipantTurnStarted && item.StepId is not null)
            .OrderBy(item => item.Sequence)
            .Select(item => (Participant: item.StepId!, item.Sequence))
            .ToArray();
        var sequence = turns
            .Where((turn, index) => index == 0 || turn.Participant != turns[index - 1].Participant)
            .ToArray();
        return sequence.Zip(sequence.Skip(1), (from, to) => (from, to))
            .Select((pair, index) => new FlowTopologyTransfer(index + 1, pair.from.Participant, pair.to.Participant, pair.to.Sequence))
            .ToArray();
    }

    private static string? LastObservedParticipant(
        IReadOnlyList<FlowRunEvent> events,
        IReadOnlyList<FlowTopologyTransfer> transfers) =>
        events
            .Where(item => item.Type == FlowRunEventType.ParticipantTurnStarted && item.StepId is not null)
            .OrderByDescending(item => item.Sequence)
            .Select(item => item.StepId)
            .FirstOrDefault()
        ?? transfers.LastOrDefault()?.To;

    private static string? PayloadString(System.Text.Json.JsonElement? payload, string propertyName) =>
        payload is { ValueKind: System.Text.Json.JsonValueKind.Object }
        && payload.Value.TryGetProperty(propertyName, out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
