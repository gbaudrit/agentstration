using System.Security.Cryptography;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed record RuntimeWorkerLeaseOptions
{
    public const string SectionName = "Agentstration:RuntimeWorker:Lease";

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(45);

    public void Validate()
    {
        if (HeartbeatInterval < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("The Runtime Worker heartbeat interval must be at least one second.");
        if (LeaseDuration < HeartbeatInterval * 3)
            throw new InvalidOperationException("The Runtime Worker lease duration must be at least three heartbeat intervals.");
        if (LeaseDuration > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("The Runtime Worker lease duration cannot exceed fifteen minutes.");
    }
}

public sealed class RuntimeWorkerAssignmentService(
    IRuntimeWorkerAssignmentStore assignments,
    TimeProvider timeProvider,
    RuntimeWorkerLeaseOptions options,
    RuntimeAssignmentAvailabilitySignal availability)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        await assignments.InitializeAsync(cancellationToken);
    }

    public Task<StoredRuntimeWorkerAssignment> CreateAsync(
        WorkspaceId workspaceId,
        string runId,
        string runtimeCapability,
        string runtimeCapabilityVersion,
        string executionMaterialVersion,
        string executionMaterialId,
        string executionMaterialDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCapability);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCapabilityVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionMaterialVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionMaterialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionMaterialDigest);
        var now = timeProvider.GetUtcNow();
        return CreateAndSignalAsync(new RuntimeWorkerAssignment
        {
            WorkspaceId = workspaceId,
            Id = new RuntimeAssignmentId(Guid.NewGuid()),
            TargetRunId = runId,
            RuntimeCapability = runtimeCapability,
            RuntimeCapabilityVersion = runtimeCapabilityVersion,
            ExecutionMaterialVersion = executionMaterialVersion,
            ExecutionMaterialId = executionMaterialId,
            ExecutionMaterialDigest = executionMaterialDigest,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);
    }

    public async Task<ClaimedRuntimeWorkerAssignment?> ClaimNextAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId workerSessionId,
        string runtimeCapability,
        IReadOnlySet<string> runtimeCapabilityVersions,
        IReadOnlySet<string> executionMaterialVersions,
        int maximumConcurrentAssignments,
        CancellationToken cancellationToken)
    {
        if (workerId.Value == Guid.Empty || workerSessionId.Value == Guid.Empty)
            throw new ArgumentException("Worker and Worker session identifiers must be non-empty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCapability);
        if (runtimeCapabilityVersions.Count == 0 || executionMaterialVersions.Count == 0)
            throw new ArgumentException("At least one Runtime capability and execution-material version is required.");
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentAssignments, 1);

        var now = timeProvider.GetUtcNow();
        var token = CreateOwnershipToken();
        var request = new RuntimeWorkerClaimRequest
        {
            AttemptId = new RuntimeAssignmentAttemptId(Guid.NewGuid()),
            WorkerId = workerId,
            WorkerSessionId = workerSessionId,
            RuntimeCapability = runtimeCapability,
            RuntimeCapabilityVersions = runtimeCapabilityVersions,
            ExecutionMaterialVersions = executionMaterialVersions,
            MaximumConcurrentAssignments = maximumConcurrentAssignments
        };
        var claimed = await assignments.ClaimNextAsync(request, Digest(token), now, now.Add(options.LeaseDuration), cancellationToken);
        if (claimed is null) return null;
        var attempt = claimed.Value.CurrentAttempt ?? throw new InvalidOperationException("The claimed assignment has no active attempt.");
        return new ClaimedRuntimeWorkerAssignment(
            claimed.Value,
            new RuntimeAssignmentOwnershipProof
            {
                WorkspaceId = claimed.Value.WorkspaceId,
                AssignmentId = claimed.Value.Id,
                AttemptId = attempt.Id,
                WorkerId = attempt.WorkerId,
                WorkerSessionId = attempt.WorkerSessionId,
                FencingGeneration = attempt.FencingGeneration,
                OwnershipToken = token
            },
            options.HeartbeatInterval);
    }

    public async Task<RuntimeAssignmentHeartbeat> HeartbeatAsync(RuntimeAssignmentOwnershipProof proof, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var renewed = await assignments.RenewAsync(proof, Digest(proof.OwnershipToken), now, now.Add(options.LeaseDuration), cancellationToken);
        return new RuntimeAssignmentHeartbeat(renewed.Value, renewed.Value.CancellationRequestedAt is not null);
    }

    public Task<RuntimeAssignmentAuthorization> AuthorizeAsync(
        RuntimeAssignmentOwnershipProof proof,
        CancellationToken cancellationToken) =>
        assignments.ValidateOwnershipAsync(proof, Digest(proof.OwnershipToken), timeProvider.GetUtcNow(), cancellationToken);

    public Task<StoredRuntimeWorkerAssignment> RequestCancellationAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        CancellationToken cancellationToken) =>
        assignments.RequestCancellationAsync(workspaceId, assignmentId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<RuntimeAssignmentTerminalResult> CompleteAsync(
        RuntimeAssignmentOwnershipProof proof,
        RuntimeAssignmentTerminalCommand command,
        CancellationToken cancellationToken) =>
        assignments.CompleteAsync(proof, Digest(proof.OwnershipToken), command, timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> ExpireLeasesAsync(int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        return assignments.ExpireLeasesAsync(timeProvider.GetUtcNow(), Math.Min(take, 1000), cancellationToken);
    }

    public Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> InterruptSupersededSessionsAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId activeSessionId,
        CancellationToken cancellationToken) =>
        assignments.InterruptSupersededSessionsAsync(workerId, activeSessionId, timeProvider.GetUtcNow(), cancellationToken);

    private async Task<StoredRuntimeWorkerAssignment> CreateAndSignalAsync(
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken)
    {
        var stored = await assignments.CreateAsync(assignment, cancellationToken);
        availability.Pulse();
        return stored;
    }

    private static string CreateOwnershipToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Digest(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    }
}
