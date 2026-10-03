using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

public sealed record AwpWorkspaceScope(Guid TenantId, string WorkspaceId);

public sealed record AwpRuntimeRequirement(
    string RuntimeKind,
    string CapabilityVersion,
    string ExecutionMaterialVersion);

public sealed record AwpExecutionMaterialReference(
    string MaterialId,
    string SchemaVersion,
    string Digest);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AwpDirectAgentRunTarget), "directAgentRun")]
[JsonDerivedType(typeof(AwpRootFlowRunTarget), "rootFlowRun")]
public abstract record AwpAssignmentTarget;

public sealed record AwpDirectAgentRunTarget(string RuntimeRunId) : AwpAssignmentTarget;

public sealed record AwpRootFlowRunTarget(string FlowRunId) : AwpAssignmentTarget;

public sealed record AwpAssignmentOwnership(
    string Token,
    long FencingGeneration,
    DateTimeOffset LeaseExpiresAt,
    int HeartbeatIntervalSeconds);

public sealed record AwpRunAssignment(
    AwpAssignmentId AssignmentId,
    AwpAssignmentAttemptId AttemptId,
    AwpWorkspaceScope Scope,
    AwpAssignmentTarget Target,
    AwpRuntimeRequirement Runtime,
    AwpExecutionMaterialReference ExecutionMaterial,
    AwpAssignmentOwnership Ownership);

public sealed record AwpClaimRequest(
    AwpWorkerId WorkerId,
    AwpWorkerSessionId SessionId,
    int AvailableCapacity,
    int MaximumWaitSeconds);

public sealed record AwpClaimResponse(DateTimeOffset ServerTime, AwpRunAssignment? Assignment);

public sealed record AwpAssignmentCommandContext(
    AwpWorkerId WorkerId,
    AwpWorkerSessionId SessionId,
    AwpAssignmentId AssignmentId,
    AwpAssignmentAttemptId AttemptId,
    string OwnershipToken,
    long FencingGeneration);

public sealed record AwpHeartbeatRequest(
    AwpAssignmentCommandContext Context,
    long LastEventSequence);

public sealed record AwpCancellationDirective(
    bool Requested,
    DateTimeOffset? RequestedAt = null,
    string? Reason = null)
{
    public static AwpCancellationDirective None { get; } = new(false);
}

public sealed record AwpHeartbeatResponse(
    DateTimeOffset ServerTime,
    DateTimeOffset LeaseExpiresAt,
    AwpCancellationDirective Cancellation);
