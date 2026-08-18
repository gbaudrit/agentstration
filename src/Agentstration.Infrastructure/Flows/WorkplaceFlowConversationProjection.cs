using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Flows;

public sealed class WorkplaceFlowConversationProjectionSink(
    IFlowRepository flows,
    IWorkplaceRepository workplace,
    IEnumerable<IWorkplaceEventSink> eventSinks,
    ILogger<WorkplaceFlowConversationProjectionSink> logger) : IFlowRunEventSink
{
    private long eventSequence;

    public async Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken)
    {
        if (runEvent.Type is not (FlowRunEventType.ParticipantTurnStarted
            or FlowRunEventType.ParticipantTurnCompleted
            or FlowRunEventType.InputRequested
            or FlowRunEventType.FlowRunCompleted))
            return;

        try
        {
            var stored = await flows.GetRunAsync(runEvent.WorkspaceId, runEvent.RunId, cancellationToken);
            if (stored is null || !Guid.TryParse(stored.Value.InteractionId, out var interactionGuid)) return;
            var interactionId = new InteractionId(interactionGuid);
            var interaction = await workplace.GetInteractionAsync(runEvent.WorkspaceId, interactionId, cancellationToken);
            if (interaction is null) return;

            var events = await flows.ListRunEventsAsync(runEvent.WorkspaceId, runEvent.RunId, 0, cancellationToken);
            var taskId = Guid.TryParse(stored.Value.WorkTaskId, out var taskGuid) ? new WorkTaskId(taskGuid) : interaction.TaskId;
            if (taskId is { } projectedTaskId)
                await ProjectActivitiesAsync(runEvent, projectedTaskId, events, cancellationToken);

            var messages = await workplace.ListMessagesAsync(runEvent.WorkspaceId, interactionId, cancellationToken);
            var projected = messages
                .Select(message => message.Metadata?.GetValueOrDefault("flowEventSequence"))
                .Where(value => value is not null)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var completed in events.Where(value => value.Type == FlowRunEventType.ParticipantTurnCompleted).OrderBy(value => value.Sequence))
            {
                var marker = completed.Sequence.ToString(CultureInfo.InvariantCulture);
                if (projected.Contains(marker) || string.IsNullOrWhiteSpace(completed.StepId)) continue;
                var previousCompletion = events.LastOrDefault(value => value.Sequence < completed.Sequence
                    && value.Type == FlowRunEventType.ParticipantTurnCompleted
                    && string.Equals(value.StepId, completed.StepId, StringComparison.Ordinal));
                var started = events.LastOrDefault(value => value.Sequence < completed.Sequence
                    && value.Sequence > (previousCompletion?.Sequence ?? 0)
                    && value.Type == FlowRunEventType.ParticipantTurnStarted
                    && string.Equals(value.StepId, completed.StepId, StringComparison.Ordinal));
                if (started is null) continue;
                var content = string.Concat(events
                    .Where(value => value.Sequence > started.Sequence && value.Sequence < completed.Sequence
                        && value.Type == FlowRunEventType.StepOutputDelta
                        && string.Equals(value.StepId, completed.StepId, StringComparison.Ordinal))
                    .Select(Content));
                if (string.IsNullOrWhiteSpace(content)) continue;

                var binding = stored.Value.RuntimeBindings.FirstOrDefault(value => string.Equals(value.ParticipantId, completed.StepId, StringComparison.Ordinal));
                var messageMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["flowRunId"] = runEvent.RunId,
                    ["participantId"] = completed.StepId,
                    ["flowEventSequence"] = marker
                };
                if (ParticipantTurn(completed) is { } participantTurn) messageMetadata["participantTurn"] = participantTurn;
                var message = new ConversationMessage(
                    DeterministicGuid($"{runEvent.WorkspaceId}:{runEvent.RunId}:{completed.Sequence}"),
                    runEvent.WorkspaceId,
                    interactionId,
                    taskId,
                    ConversationRole.Agentstration,
                    content.Trim(),
                    completed.Timestamp,
                    binding?.AgentResourceId,
                    Metadata: messageMetadata);
                await workplace.AddMessageAsync(message, cancellationToken);
                foreach (var sink in eventSinks)
                    await sink.PublishAsync(new MessageAddedEvent(EventId(), runEvent.WorkspaceId.Value, Sequence(), completed.Timestamp, message), cancellationToken);
                projected.Add(marker);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not project participant turns for Flow Run {FlowRunId} into Workplace", runEvent.RunId);
        }
    }

    private async Task ProjectActivitiesAsync(
        FlowRunEvent runEvent,
        WorkTaskId taskId,
        IReadOnlyList<FlowRunEvent> events,
        CancellationToken cancellationToken)
    {
        var activities = await workplace.ListActivitiesAsync(runEvent.WorkspaceId, taskId, cancellationToken);
        var projected = activities
            .Select(activity => activity.Metadata?.GetValueOrDefault("flowEventSequence"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var turn in events
            .Where(value => value.Type is FlowRunEventType.ParticipantTurnStarted or FlowRunEventType.ParticipantTurnCompleted)
            .OrderBy(value => value.Sequence))
        {
            var marker = turn.Sequence.ToString(CultureInfo.InvariantCulture);
            if (projected.Contains(marker) || string.IsNullOrWhiteSpace(turn.StepId)) continue;
            var started = turn.Type == FlowRunEventType.ParticipantTurnStarted;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["participantId"] = turn.StepId,
                ["flowEventSequence"] = marker
            };
            if (ParticipantTurn(turn) is { } participantTurn) metadata["participantTurn"] = participantTurn;
            var activity = new WorkTaskActivity(
                new WorkTaskActivityId(DeterministicGuid($"activity:{runEvent.WorkspaceId}:{runEvent.RunId}:{turn.Sequence}")),
                runEvent.WorkspaceId,
                taskId,
                started ? WorkTaskActivityType.ProgressStarted : WorkTaskActivityType.ProgressCompleted,
                started ? "Preparing a response" : "Response prepared",
                null,
                turn.Timestamp,
                WorkActorKind.Agentstration,
                runEvent.RunId,
                metadata);
            await workplace.AddActivityAsync(activity, cancellationToken);
            foreach (var sink in eventSinks)
                await sink.PublishAsync(new TaskActivityAddedEvent(EventId(), runEvent.WorkspaceId.Value, Sequence(), turn.Timestamp, activity), cancellationToken);
            projected.Add(marker);
        }
    }

    private static string? ParticipantTurn(FlowRunEvent runEvent) => runEvent.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload
        && payload.TryGetProperty("turn", out var turn)
        && turn.TryGetInt32(out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string Content(FlowRunEvent runEvent)
    {
        if (runEvent.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload
            || !payload.TryGetProperty("content", out var content)
            || content.ValueKind != System.Text.Json.JsonValueKind.String)
            return string.Empty;
        return content.GetString() ?? string.Empty;
    }

    private long Sequence() => Interlocked.Increment(ref eventSequence);
    private static string EventId() => Guid.NewGuid().ToString("N");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
