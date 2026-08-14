using Agentstration.Flow;
using Agentstration.Flow.Contracts;

namespace Agentstration.Web.Console;

public sealed class OrchestrationEditorModel
{
    public string Name { get; set; } = "multi-agent-review";
    public string? Description { get; set; } = "Coordinates multiple agents to produce a reviewed result.";
    public string Version { get; set; } = "0.1.0";
    public bool Enabled { get; set; } = true;
    public FlowOrchestrationStrategy Strategy { get; set; } = FlowOrchestrationStrategy.Sequential;
    public List<string> ParticipantIds { get; } = [];
    public bool IncludeFullHistory { get; set; } = true;
    public string InitialParticipant { get; set; } = string.Empty;
    public List<FlowHandoffEditorRoute> Handoffs { get; } = [];
    public bool Autonomous { get; set; }
    public int MaximumTurnsPerParticipant { get; set; } = 10;
    public string? TerminationPhrase { get; set; }
    public int MaximumIterations { get; set; } = 5;
    public string ManagerId { get; set; } = string.Empty;
    public int MaximumRounds { get; set; } = 10;
    public int MaximumStalls { get; set; } = 3;
    public int MaximumResets { get; set; } = 2;

    public OrchestrationFlowDefinition CreateDefinition()
    {
        if (ParticipantIds.Count < 2)
            throw new InvalidOperationException("Select at least two participants.");
        if (ParticipantIds.Distinct(StringComparer.Ordinal).Count() != ParticipantIds.Count)
            throw new InvalidOperationException("Each participant can only be selected once.");

        var participants = ParticipantIds
            .Select(id => new FlowTargetReference(FlowTargetKind.Agent, id))
            .ToArray();
        FlowOrchestrationPattern pattern = Strategy switch
        {
            FlowOrchestrationStrategy.Sequential => new SequentialOrchestrationPattern(IncludeFullHistory),
            FlowOrchestrationStrategy.Concurrent => new ConcurrentOrchestrationPattern(),
            FlowOrchestrationStrategy.Handoff => CreateHandoffPattern(),
            FlowOrchestrationStrategy.GroupChat => new GroupChatOrchestrationPattern(MaximumIterations),
            FlowOrchestrationStrategy.Magentic => CreateMagenticPattern(),
            _ => throw new InvalidOperationException("The selected orchestration strategy is not supported.")
        };
        return new OrchestrationFlowDefinition(participants, pattern);
    }

    public static OrchestrationEditorModel From(FlowResponse flow)
    {
        if (flow.Definition is not OrchestrationFlowDefinition definition)
            throw new InvalidOperationException($"Flow '{flow.Id}' is not an orchestration.");
        var model = new OrchestrationEditorModel
        {
            Name = flow.Name,
            Description = flow.Description,
            Version = flow.Version,
            Enabled = flow.Enabled,
            Strategy = definition.Strategy
        };
        model.ParticipantIds.AddRange(definition.Participants.Select(participant => participant.Id));
        switch (definition.Pattern)
        {
            case SequentialOrchestrationPattern sequential:
                model.IncludeFullHistory = sequential.IncludeFullHistory;
                break;
            case HandoffOrchestrationPattern handoff:
                model.InitialParticipant = handoff.InitialParticipant;
                model.Handoffs.AddRange(handoff.Handoffs.Select(route => new FlowHandoffEditorRoute(route.From, route.To)));
                model.Autonomous = handoff.Autonomous;
                model.MaximumTurnsPerParticipant = handoff.MaximumTurnsPerParticipant;
                model.TerminationPhrase = handoff.TerminationPhrase;
                break;
            case GroupChatOrchestrationPattern groupChat:
                model.MaximumIterations = groupChat.MaximumIterations;
                break;
            case MagenticOrchestrationPattern magentic:
                model.ManagerId = magentic.Manager.Id;
                model.MaximumRounds = magentic.MaximumRounds;
                model.MaximumStalls = magentic.MaximumStalls;
                model.MaximumResets = magentic.MaximumResets;
                break;
        }
        model.EnsureStrategyDefaults();
        return model;
    }

    public void EnsureStrategyDefaults()
    {
        if (string.IsNullOrWhiteSpace(InitialParticipant) || !ParticipantIds.Contains(InitialParticipant, StringComparer.Ordinal))
            InitialParticipant = ParticipantIds.FirstOrDefault() ?? string.Empty;
        if (Handoffs.Count == 0 && ParticipantIds.Count >= 2)
            Handoffs.AddRange(ParticipantIds.Zip(ParticipantIds.Skip(1), (from, to) => new FlowHandoffEditorRoute(from, to)));
    }

    private HandoffOrchestrationPattern CreateHandoffPattern()
    {
        if (string.IsNullOrWhiteSpace(InitialParticipant))
            throw new InvalidOperationException("Select the initial handoff participant.");
        if (Handoffs.Count == 0)
            throw new InvalidOperationException("Add at least one handoff route.");
        return new HandoffOrchestrationPattern(
            InitialParticipant,
            Handoffs.Select(route => new FlowHandoff(route.From, route.To)).ToArray(),
            Autonomous,
            MaximumTurnsPerParticipant,
            string.IsNullOrWhiteSpace(TerminationPhrase) ? null : TerminationPhrase.Trim());
    }

    private MagenticOrchestrationPattern CreateMagenticPattern()
    {
        if (string.IsNullOrWhiteSpace(ManagerId))
            throw new InvalidOperationException("Select a manager for the Magentic orchestration.");
        if (ParticipantIds.Contains(ManagerId, StringComparer.Ordinal))
            throw new InvalidOperationException("The Magentic manager must be distinct from the participants.");
        return new MagenticOrchestrationPattern(
            new FlowTargetReference(FlowTargetKind.Agent, ManagerId),
            MaximumRounds,
            MaximumStalls,
            MaximumResets);
    }
}

public sealed class FlowHandoffEditorRoute(string from, string to)
{
    public string From { get; set; } = from;
    public string To { get; set; } = to;
}
