using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.MicrosoftAgentFramework;

#pragma warning disable MAAIW001
public sealed partial class AgentFrameworkFlowOrchestrationEngine(
    IRuntimeAgentResolver agentResolver,
    IToolCatalog tools,
    AgentFrameworkRuntimeFactory agentFactory,
    IRuntimeExecutionStateStore? executionStates = null,
    TimeProvider? timeProvider = null,
    IToolExecutionPipeline? configuredToolExecution = null) : IFlowOrchestrationEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CheckpointAvailabilityPollInterval = TimeSpan.FromMilliseconds(10);
    private const int CheckpointAvailabilityPollAttempts = 1000;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IToolExecutionPipeline toolExecution = configuredToolExecution ?? UnavailableToolExecutionPipeline.Instance;

































    private sealed record BuiltWorkflow(Workflow Workflow, ResolvedParticipant? Manager);
    internal sealed record InteractionDescription(InputRequestType Type, string Prompt);
    private sealed record ResolvedParticipant(
        string Id,
        ResolvedRuntimeAgent Resolution,
        AIAgent Agent,
        RuntimeExecutionBinding Binding);
    private sealed record WorkflowActor(string Id, bool IsManager);

    private sealed class ParticipantState(ResolvedParticipant participant, string? terminationPhrase)
    {
        private readonly HashSet<string> completedResponseIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> tools = new(StringComparer.Ordinal);

        public ResolvedParticipant Participant { get; } = participant;
        public MutableTurn? Active { get; private set; }
        public List<MutableTurn> Turns { get; } = [];

        public bool IsCompleted(string responseId) => completedResponseIds.Contains(responseId);

        public void Start(int turn, string responseId) => Active = new MutableTurn(turn, responseId, terminationPhrase);

        public void Correlate(string responseId) => Active!.ResponseIds.Add(responseId);

        public string Append(string content) => Active!.Append(content);

        public void Capture(IEnumerable<AIContent> contents)
        {
            foreach (var content in contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    if (!IsInternalOrchestrationTool(functionCall.Name))
                        tools.Add(functionCall.Name);
                }
                if (content is UsageContent usage) MergeUsage(usage.Details);
            }
        }
        public void MergeUsage(UsageDetails? usage)
        {
            if (usage is null || Active is null) return;
            if (usage.InputTokenCount is { } inputTokens)
                Active.InputTokens = Math.Max(Active.InputTokens ?? 0, inputTokens);
            if (usage.OutputTokenCount is { } outputTokens)
                Active.OutputTokens = Math.Max(Active.OutputTokens ?? 0, outputTokens);
            if (usage.TotalTokenCount is { } totalTokens)
                Active.TotalTokens = Math.Max(Active.TotalTokens ?? 0, totalTokens);
        }

        public CompletedTurn CompleteActive()
        {
            var completed = Active ?? throw new InvalidOperationException("No participant turn is active.");
            Active = null;
            var delta = completed.Flush();
            completedResponseIds.UnionWith(completed.ResponseIds);
            var duplicateEmptyTurn = completed.Content.Length == 0
                && Turns.LastOrDefault()?.Content.Length == 0;
            var include = !duplicateEmptyTurn && !completed.IsTerminationMarkerOnly;
            if (include) Turns.Add(completed);
            return new(completed, delta);
        }

        public FlowParticipantResult ToResult()
        {
            var turns = Turns.Select(turn => new FlowParticipantTurnResult(turn.Turn, turn.Content.ToString())).ToArray();
            var inputTokens = Sum(Turns.Select(turn => turn.InputTokens));
            var outputTokens = Sum(Turns.Select(turn => turn.OutputTokens));
            var totalTokens = Sum(Turns.Select(turn => turn.TotalTokens));
            var usage = inputTokens is null && outputTokens is null && totalTokens is null
                ? null
                : new FlowStepRunUsage(ToInt32(inputTokens), ToInt32(outputTokens), ToInt32(totalTokens));
            return new FlowParticipantResult(
                Participant.Id,
                turns,
                JsonSerializer.SerializeToElement(turns.LastOrDefault()?.Content ?? string.Empty),
                Participant.Resolution.AgentName,
                Participant.Resolution.Definition.AgentVersion,
                Participant.Resolution.ModelProfileName,
                null,
                tools.Union(Participant.Resolution.Definition.EffectiveToolNames, StringComparer.Ordinal).Order().ToArray(),
                usage);
        }

        private static long? Sum(IEnumerable<long?> values)
        {
            var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
            return known.Length == 0 ? null : known.Sum();
        }

        private static int? ToInt32(long? value) => value is null ? null
            : value > int.MaxValue ? int.MaxValue : checked((int)value.Value);
    }

    private sealed record CompletedTurn(MutableTurn Turn, string Delta);

    private sealed class MutableTurn(int turn, string responseId, string? terminationPhrase)
    {
        private readonly TerminationPhraseFilter? terminationFilter = string.IsNullOrWhiteSpace(terminationPhrase)
            ? null
            : new(terminationPhrase);

        public int Turn { get; } = turn;
        public string ResponseId { get; } = responseId;
        public HashSet<string> ResponseIds { get; } = new(StringComparer.Ordinal) { responseId };
        public StringBuilder Content { get; } = new();
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public long? TotalTokens { get; set; }
        public bool IsTerminationMarkerOnly => terminationFilter?.Matched == true && Content.Length == 0;

        public string Append(string content)
        {
            var visible = terminationFilter?.Append(content) ?? content;
            Content.Append(visible);
            return visible;
        }

        public string Flush()
        {
            var visible = terminationFilter?.Flush() ?? string.Empty;
            Content.Append(visible);
            return visible;
        }
    }

    internal sealed class TerminationPhraseFilter(string phrase)
    {
        private readonly StringBuilder pending = new();
        public bool Matched { get; private set; }

        public string Append(string content)
        {
            pending.Append(content);
            var visible = new StringBuilder();
            while (pending.Length > 0)
            {
                var value = pending.ToString();
                var marker = value.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    Matched = true;
                    visible.Append(value.AsSpan(0, marker));
                    pending.Clear().Append(value.AsSpan(marker + phrase.Length));
                    continue;
                }

                var retained = RetainedSuffixLength(value);
                visible.Append(value.AsSpan(0, value.Length - retained));
                pending.Clear().Append(value.AsSpan(value.Length - retained));
                break;
            }
            return visible.ToString();
        }

        public string Flush()
        {
            var visible = pending.ToString();
            pending.Clear();
            return visible;
        }

        private int RetainedSuffixLength(string value)
        {
            var maximum = Math.Min(value.Length, phrase.Length - 1);
            for (var length = maximum; length > 0; length--)
                if (value.AsSpan(value.Length - length).Equals(phrase.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                    return length;
            return 0;
        }
    }
}
#pragma warning restore MAAIW001
