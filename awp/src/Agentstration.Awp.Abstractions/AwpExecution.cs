using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

public sealed record AwpFlowStepLocation(
    string FlowRunId,
    string FlowVersion,
    string FlowDefinitionHash,
    string StepDefinitionId,
    string StepName,
    string StepType,
    int DefinitionPosition,
    AwpStepExecutionId StepExecutionId);

public sealed record AwpTurnAttemptReference
{
    [JsonConstructor]
    public AwpTurnAttemptReference(AwpTurnAttemptId turnAttemptId, int turnAttemptNumber)
    {
        if (turnAttemptNumber != 1)
            throw new ArgumentOutOfRangeException(nameof(turnAttemptNumber), "AWP v1 supports only the initial Turn attempt numbered 1.");
        TurnAttemptId = turnAttemptId;
        TurnAttemptNumber = turnAttemptNumber;
    }

    public AwpTurnAttemptId TurnAttemptId { get; }
    public int TurnAttemptNumber { get; }
}

public sealed record AwpAgentTurnLocation(
    AwpTurnId TurnId,
    AwpTurnAttemptReference Attempt,
    string? ParticipantId = null);

public sealed record AwpExecutionLocation(
    string RunId,
    AwpFlowStepLocation? FlowStep = null,
    AwpAgentTurnLocation? AgentTurn = null);

[JsonConverter(typeof(AwpCamelCaseEnumConverter<AwpExecutionEventKind>))]
public enum AwpExecutionEventKind
{
    AssignmentStarted,
    RunStarted,
    RunCompleted,
    StepStarted,
    StepCompleted,
    TurnStarted,
    TurnCompleted,
    ResponseDelta,
    ToolCallStarted,
    ToolCallCompleted,
    ToolCallFailed,
    CheckpointPersisted,
    WaitingForInput,
    Diagnostic
}

public sealed record AwpExecutionEvent(
    AwpEventId EventId,
    long AttemptEventSequence,
    DateTimeOffset OccurredAt,
    AwpExecutionEventKind Kind,
    AwpExecutionLocation Location,
    AwpToolCallId? ToolCallId = null,
    JsonElement? Payload = null);

public sealed record AwpAppendEventsRequest(
    AwpAssignmentCommandContext Context,
    IReadOnlyList<AwpExecutionEvent> Events);

public sealed record AwpAppendEventsResponse(
    DateTimeOffset ServerTime,
    long AcceptedThroughSequence,
    IReadOnlyList<AwpEventId> DuplicateEventIds);

public sealed record AwpOpenStepExecutionRequest(
    AwpAssignmentCommandContext Context,
    string FlowRunId,
    string FlowVersion,
    string FlowDefinitionHash,
    string StepDefinitionId);

public sealed record AwpOpenStepExecutionResponse(
    DateTimeOffset ServerTime,
    AwpFlowStepLocation Step);

public sealed record AwpOpenTurnRequest(
    AwpAssignmentCommandContext Context,
    string RunId,
    AwpFlowStepLocation? FlowStep = null,
    string? ParticipantId = null);

public sealed record AwpOpenTurnResponse(
    DateTimeOffset ServerTime,
    AwpAgentTurnLocation Turn);

[JsonConverter(typeof(AwpCamelCaseEnumConverter<AwpExecutionFailureKind>))]
public enum AwpExecutionFailureKind
{
    Execution,
    Cancelled,
    TimedOut,
    Protocol
}

public sealed record AwpExecutionFailure(
    AwpExecutionFailureKind Kind,
    string Code,
    string Message,
    JsonElement? Details = null);

public sealed record AwpCompleteAssignmentRequest(
    AwpAssignmentCommandContext Context,
    AwpEventId CommandId,
    JsonElement? Output = null);

public sealed record AwpFailAssignmentRequest(
    AwpAssignmentCommandContext Context,
    AwpEventId CommandId,
    AwpExecutionFailure Failure);

[JsonConverter(typeof(AwpCamelCaseEnumConverter<AwpTerminalState>))]
public enum AwpTerminalState
{
    Succeeded,
    Failed,
    Cancelled
}

public sealed record AwpTerminalResponse(
    DateTimeOffset ServerTime,
    AwpTerminalState State,
    long LastEventSequence);

public sealed class AwpCamelCaseEnumConverter<TEnum>()
    : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;
