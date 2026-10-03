using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public enum RuntimeAssignmentTargetKind { RuntimeRun }
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
    public required RuntimeAssignmentId Id { get; init; }
    public RuntimeAssignmentTargetKind TargetKind { get; init; } = RuntimeAssignmentTargetKind.RuntimeRun;
    public required string TargetRunId { get; init; }
    public required string RuntimeCapability { get; init; }
    public required string RuntimeCapabilityVersion { get; init; }
    public required string ExecutionMaterialVersion { get; init; }
    public RuntimeAssignmentState State { get; init; } = RuntimeAssignmentState.Pending;
    public long FencingGeneration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public IReadOnlyList<RuntimeAssignmentAttempt> Attempts { get; init; } = [];
    public RuntimeAssignmentAttempt? CurrentAttempt => Attempts.LastOrDefault();
}

public sealed record StoredRuntimeWorkerAssignment(RuntimeWorkerAssignment Value, string ETag);

public sealed record RuntimeWorkerClaimRequest
{
    public required RuntimeAssignmentAttemptId AttemptId { get; init; }
    public required RuntimeWorkerId WorkerId { get; init; }
    public required RuntimeWorkerSessionId WorkerSessionId { get; init; }
    public required string RuntimeCapability { get; init; }
    public required IReadOnlySet<string> RuntimeCapabilityVersions { get; init; }
    public required IReadOnlySet<string> ExecutionMaterialVersions { get; init; }
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
}

public interface IRuntimeWorkerAssignmentStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment> CreateAsync(RuntimeWorkerAssignment assignment, CancellationToken cancellationToken);
    Task<StoredRuntimeWorkerAssignment?> GetAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId, CancellationToken cancellationToken);
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
    Task<StoredRuntimeWorkerAssignment> RequestCancellationAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);
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
}

public sealed class RuntimeAssignmentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
