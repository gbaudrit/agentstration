using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public enum RuntimeAssignmentTargetKind { RuntimeRun, FlowRun }
public enum RuntimeAssignmentState { Pending, Assigned, Succeeded, Failed, Cancelled }
public enum RuntimeAssignmentAttemptState { Active, Succeeded, Failed, Interrupted }
public enum RuntimeAssignmentTerminalOutcome { Succeeded, Failed }

public readonly record struct RuntimeAssignmentId(Guid Value);
public readonly record struct RuntimeAssignmentAttemptId(Guid Value);
public readonly record struct RuntimeWorkerId(Guid Value);
public readonly record struct RuntimeWorkerSessionId(Guid Value);

public sealed record RuntimeAssignmentAttempt
{
    public required RuntimeAssignmentAttemptId Id { get; init; }
    public required RuntimeWorkerId WorkerId { get; init; }
    public required RuntimeWorkerSessionId WorkerSessionId { get; init; }
    public required long FencingGeneration { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required DateTimeOffset LeaseExpiresAt { get; init; }
    public required DateTimeOffset LastHeartbeatAt { get; init; }
    public required RuntimeAssignmentAttemptState State { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? TerminalEventId { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record RuntimeWorkerAssignment
{
    public required WorkspaceId WorkspaceId { get; init; }
    public Guid TenantId { get; init; }
    public required RuntimeAssignmentId Id { get; init; }
    public RuntimeAssignmentTargetKind TargetKind { get; init; } = RuntimeAssignmentTargetKind.RuntimeRun;
    public required string TargetRunId { get; init; }
    public required string RuntimeCapability { get; init; }
    public required string RuntimeCapabilityVersion { get; init; }
    public required string ExecutionMaterialVersion { get; init; }
    public required string ExecutionMaterialId { get; init; }
    public required string ExecutionMaterialDigest { get; init; }
    public RuntimeAssignmentState State { get; init; } = RuntimeAssignmentState.Pending;
    public long FencingGeneration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public IReadOnlyList<RuntimeAssignmentAttempt> Attempts { get; init; } = [];
    public IReadOnlyList<RuntimeAssignmentStepExecution> StepExecutions { get; init; } = [];
    public IReadOnlyList<RuntimeAssignmentTurn> Turns { get; init; } = [];
    public IReadOnlyList<RuntimeAssignmentExecutionEvent> ExecutionEvents { get; init; } = [];
    public IReadOnlyList<RuntimeAssignmentCheckpoint> Checkpoints { get; init; } = [];
    public IReadOnlyList<string> ChildFlowRunIds { get; init; } = [];
    public RuntimeAssignmentAttempt? CurrentAttempt => Attempts.LastOrDefault();
}

public sealed record StoredRuntimeWorkerAssignment(RuntimeWorkerAssignment Value, string ETag);

public sealed record RuntimeWorkerAssignmentQuery
{
    public WorkspaceId? WorkspaceId { get; init; }
    public RuntimeWorkerId? WorkerId { get; init; }
    public IReadOnlySet<RuntimeAssignmentState>? States { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 200;
}

public sealed record RuntimeWorkerClaimRequest
{
    public required RuntimeAssignmentAttemptId AttemptId { get; init; }
    public required RuntimeWorkerId WorkerId { get; init; }
    public required RuntimeWorkerSessionId WorkerSessionId { get; init; }
    public required string RuntimeCapability { get; init; }
    public required IReadOnlySet<string> RuntimeCapabilityVersions { get; init; }
    public required IReadOnlySet<string> ExecutionMaterialVersions { get; init; }
    public required int MaximumConcurrentAssignments { get; init; }
}

public sealed record RuntimeAssignmentOwnershipProof
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required RuntimeAssignmentId AssignmentId { get; init; }
    public required RuntimeAssignmentAttemptId AttemptId { get; init; }
    public required RuntimeWorkerId WorkerId { get; init; }
    public required RuntimeWorkerSessionId WorkerSessionId { get; init; }
    public required long FencingGeneration { get; init; }
    public required string OwnershipToken { get; init; }
}

public sealed record ClaimedRuntimeWorkerAssignment(
    RuntimeWorkerAssignment Assignment,
    RuntimeAssignmentOwnershipProof Ownership,
    TimeSpan HeartbeatInterval);

public sealed record RuntimeAssignmentHeartbeat(
    RuntimeWorkerAssignment Assignment,
    bool CancellationRequested);

public sealed record RuntimeAssignmentAuthorization(
    RuntimeWorkerAssignment Assignment,
    bool CancellationRequested,
    TimeSpan LeaseRemaining);

public sealed record RuntimeAssignmentTerminalResult(
    RuntimeWorkerAssignment Assignment,
    RuntimeRunState RunState,
    bool IdempotentReplay);

public sealed record RuntimeAssignmentStepExecution
{
    public required Guid CommandId { get; init; }
    public required Guid Id { get; init; }
    public required string FlowRunId { get; init; }
    public required string FlowVersion { get; init; }
    public required string FlowDefinitionHash { get; init; }
    public required string StepDefinitionId { get; init; }
    public required string StepName { get; init; }
    public required string StepType { get; init; }
    public required int DefinitionPosition { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
}

public sealed record RuntimeAssignmentTurn
{
    public required Guid CommandId { get; init; }
    public required Guid Id { get; init; }
    public required Guid AttemptId { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public required string RunId { get; init; }
    public Guid? StepExecutionId { get; init; }
    public string? ParticipantId { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
}

public sealed record RuntimeAssignmentExecutionEvent
{
    public required Guid EventId { get; init; }
    public required long AttemptEventSequence { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Kind { get; init; }
    public required string RunId { get; init; }
    public Guid? StepExecutionId { get; init; }
    public Guid? TurnId { get; init; }
    public Guid? TurnAttemptId { get; init; }
    public Guid? ToolCallId { get; init; }
    public System.Text.Json.JsonElement? Payload { get; init; }
}

public sealed record RuntimeAssignmentEventAppendResult(
    long AcceptedThroughSequence,
    IReadOnlyList<Guid> DuplicateEventIds);

public sealed record RuntimeAssignmentCheckpoint(
    string CheckpointId,
    string SchemaVersion,
    string CompatibilityKey,
    System.Text.Json.JsonElement Payload,
    DateTimeOffset PersistedAt);

public sealed record RuntimeAssignmentTerminalCommand(
    RuntimeAssignmentTerminalOutcome Outcome,
    Guid EventId,
    string? Response = null,
    string? Error = null,
    string? ErrorCode = null);

public static class RuntimeAssignmentErrorCodes
{
    public const string NotOwned = "assignment_not_owned";
    public const string LeaseExpired = "assignment_lease_expired";
    public const string FencingRejected = "assignment_fencing_rejected";
    public const string TerminalConflict = "assignment_terminal_conflict";
    public const string InvalidCoordinate = "assignment_invalid_coordinate";
    public const string InvalidEventSequence = "assignment_invalid_event_sequence";
    public const string ReplayConflict = "assignment_replay_conflict";
    public const string LimitExceeded = "assignment_limit_exceeded";
    public const string LeaseTooShort = "assignment_lease_too_short";
}

public interface IRuntimeWorkerAssignmentStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment> CreateAsync(RuntimeWorkerAssignment assignment, CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment?> GetAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId, CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment?> GetByTargetAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentTargetKind targetKind,
        string targetRunId,
        CancellationToken cancellationToken) => Task.FromResult<StoredRuntimeWorkerAssignment?>(null);
    Task<IReadOnlyList<StoredRuntimeWorkerAssignment>> ListAsync(
        RuntimeWorkerAssignmentQuery query,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredRuntimeWorkerAssignment>>([]);
    Task<StoredRuntimeWorkerAssignment?> ClaimNextAsync(
        RuntimeWorkerClaimRequest request,
        byte[] ownershipTokenDigest,
        DateTimeOffset acquiredAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment> RenewAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        DateTimeOffset renewedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken);
    Task<RuntimeAssignmentAuthorization> ValidateOwnershipAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task<RuntimeAssignmentStepExecution> OpenStepExecutionAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentStepExecution step,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task<RuntimeAssignmentTurn> OpenTurnAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentTurn turn,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task RegisterChildFlowAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        string childFlowRunId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) => Task.CompletedTask;
    Task<RuntimeAssignmentEventAppendResult> AppendEventsAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task StoreCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentCheckpoint checkpoint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task<RuntimeAssignmentCheckpoint?> GetCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        string checkpointId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment> RequestCancellationAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment> RequeueAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken) =>
        Task.FromException<StoredRuntimeWorkerAssignment>(new NotSupportedException("Assignment requeue is not supported by this store."));
    Task<RuntimeAssignmentTerminalResult> CompleteAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentTerminalCommand command,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> ExpireLeasesAsync(
        DateTimeOffset observedAt,
        int take,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> InterruptSupersededSessionsAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId activeSessionId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

public interface IRuntimeAssignmentProjection
{
    Task ProjectEventsAsync(
        RuntimeWorkerAssignment assignment,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events,
        CancellationToken cancellationToken);

    Task ProjectTerminalAsync(
        RuntimeAssignmentTerminalResult result,
        CancellationToken cancellationToken);
}

public sealed class NullRuntimeAssignmentProjection : IRuntimeAssignmentProjection
{
    public Task ProjectEventsAsync(RuntimeWorkerAssignment assignment,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ProjectTerminalAsync(RuntimeAssignmentTerminalResult result,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class RuntimeAssignmentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
