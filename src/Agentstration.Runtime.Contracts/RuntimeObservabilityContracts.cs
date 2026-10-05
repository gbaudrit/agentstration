using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Contracts;

public enum RuntimeWorkerPresenceState { Online, Stale, Offline, Unknown }

public sealed record RuntimeWorkerCapabilityResponse(
    string RuntimeKind,
    string CapabilityVersion,
    IReadOnlyList<string> ExecutionMaterialVersions,
    string? RuntimeImplementationVersion);

public sealed record RuntimeWorkerSummaryResponse(
    Guid WorkerId,
    string DisplayName,
    string ProtocolVersion,
    string EnrollmentState,
    RuntimeWorkerPresenceState Presence,
    Guid? SessionId,
    string? SoftwareVersion,
    int? MaximumConcurrentAssignments,
    int ActiveAssignments,
    DateTimeOffset? RegisteredAt,
    DateTimeOffset? LastSeenAt,
    IReadOnlyList<RuntimeWorkerCapabilityResponse> Capabilities);

public sealed record RuntimeAssignmentAttemptResponse(
    Guid AttemptId,
    Guid WorkerId,
    Guid WorkerSessionId,
    long FencingGeneration,
    string State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);

public sealed record RuntimeAssignmentPlacementResponse(
    Guid AssignmentId,
    string TargetKind,
    string RunId,
    string State,
    string RuntimeCapability,
    string RuntimeCapabilityVersion,
    string ExecutionMaterialVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RuntimeAssignmentAttemptResponse> Attempts);

public sealed record RuntimeWorkerDetailsResponse(
    RuntimeWorkerSummaryResponse Worker,
    IReadOnlyList<RuntimeAssignmentPlacementResponse> RecentAssignments);

public sealed record AgentInstanceResponse(
    string InstanceId,
    ResourceNamespace AgentNamespace,
    string AgentName,
    long AgentGeneration,
    string RevisionId,
    string RuntimeProfile,
    string RunKind,
    string RunId,
    string? ParticipantId,
    Guid? StepExecutionId,
    int TurnCount,
    Guid AssignmentId,
    Guid AttemptId,
    Guid WorkerId,
    Guid WorkerSessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset LeaseExpiresAt);

public interface IRuntimeObservabilityQueryService
{
    Task<IReadOnlyList<RuntimeWorkerSummaryResponse>> ListWorkersAsync(CancellationToken cancellationToken);
    Task<RuntimeWorkerDetailsResponse?> GetWorkerAsync(Guid workerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentInstanceResponse>> ListAgentInstancesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<RuntimeAssignmentPlacementResponse?> GetPlacementAsync(WorkspaceId workspaceId, RuntimeAssignmentTargetKind targetKind,
        string runId, CancellationToken cancellationToken);
}
