using System.Security.Cryptography;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed record RuntimeWorkerLeaseOptions
{
    public const string SectionName = "Agentstration:RuntimeWorker:Lease";

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan MinimumSideEffectLeaseRemaining { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (HeartbeatInterval < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("The Runtime Worker heartbeat interval must be at least one second.");
        if (LeaseDuration < HeartbeatInterval * 3)
            throw new InvalidOperationException("The Runtime Worker lease duration must be at least three heartbeat intervals.");
        if (LeaseDuration > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("The Runtime Worker lease duration cannot exceed fifteen minutes.");
        if (MinimumSideEffectLeaseRemaining < TimeSpan.FromSeconds(1)
            || MinimumSideEffectLeaseRemaining >= LeaseDuration)
            throw new InvalidOperationException("The side-effect lease safety margin must be at least one second and shorter than the lease duration.");
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
        => CreateAsync(workspaceId, Guid.Empty, RuntimeAssignmentTargetKind.RuntimeRun, runId, runtimeCapability,
            runtimeCapabilityVersion, executionMaterialVersion, executionMaterialId, executionMaterialDigest, cancellationToken);

    public Task<StoredRuntimeWorkerAssignment> CreateAsync(
        WorkspaceId workspaceId,
        Guid tenantId,
        RuntimeAssignmentTargetKind targetKind,
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
        if (targetKind == RuntimeAssignmentTargetKind.FlowRun && tenantId == Guid.Empty)
            throw new ArgumentException("A Flow assignment requires its Tenant identity.", nameof(tenantId));
        var now = timeProvider.GetUtcNow();
        return CreateAndSignalAsync(new RuntimeWorkerAssignment
        {
            WorkspaceId = workspaceId,
            TenantId = tenantId,
            Id = new RuntimeAssignmentId(Guid.NewGuid()),
            TargetKind = targetKind,
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

    public Task<RuntimeAssignmentStepExecution> OpenStepExecutionAsync(
        RuntimeAssignmentOwnershipProof proof,
        string flowRunId,
        string flowVersion,
        string flowDefinitionHash,
        string stepDefinitionId,
        string stepName,
        string stepType,
        int definitionPosition,
        CancellationToken cancellationToken) => OpenStepExecutionAsync(proof, Guid.NewGuid(), flowRunId, flowVersion,
            flowDefinitionHash, stepDefinitionId, stepName, stepType, definitionPosition, cancellationToken);

    public Task<RuntimeAssignmentStepExecution> OpenStepExecutionAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid commandId,
        string flowRunId,
        string flowVersion,
        string flowDefinitionHash,
        string stepDefinitionId,
        string stepName,
        string stepType,
        int definitionPosition,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The command identity must not be empty.", nameof(commandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(flowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowDefinitionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepType);
        ArgumentOutOfRangeException.ThrowIfNegative(definitionPosition);
        var now = timeProvider.GetUtcNow();
        return assignments.OpenStepExecutionAsync(proof, Digest(proof.OwnershipToken), new RuntimeAssignmentStepExecution
        {
            CommandId = commandId,
            Id = Guid.NewGuid(),
            FlowRunId = flowRunId,
            FlowVersion = flowVersion,
            FlowDefinitionHash = flowDefinitionHash,
            StepDefinitionId = stepDefinitionId,
            StepName = stepName,
            StepType = stepType,
            DefinitionPosition = definitionPosition,
            OpenedAt = now
        }, now, cancellationToken);
    }

    public Task<RuntimeAssignmentTurn> OpenTurnAsync(
        RuntimeAssignmentOwnershipProof proof,
        string runId,
        Guid? stepExecutionId,
        string? participantId,
        CancellationToken cancellationToken) => OpenTurnAsync(proof, Guid.NewGuid(), runId, stepExecutionId, participantId, cancellationToken);

    public Task<RuntimeAssignmentTurn> OpenTurnAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid commandId,
        string runId,
        Guid? stepExecutionId,
        string? participantId,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The command identity must not be empty.", nameof(commandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var now = timeProvider.GetUtcNow();
        return assignments.OpenTurnAsync(proof, Digest(proof.OwnershipToken), new RuntimeAssignmentTurn
        {
            CommandId = commandId,
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            AttemptNumber = 1,
            RunId = runId,
            StepExecutionId = stepExecutionId,
            ParticipantId = participantId,
            OpenedAt = now
        }, now, cancellationToken);
    }

    public Task<RuntimeAssignmentEventAppendResult> AppendEventsAsync(
        RuntimeAssignmentOwnershipProof proof,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Any(value => string.IsNullOrWhiteSpace(value.Kind) || value.Kind.Length > 128
            || value.Payload?.GetRawText().Length > 65_536))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded,
                "Event kinds are limited to 128 characters and individual payloads to 64 KiB.");
        return assignments.AppendEventsAsync(proof, Digest(proof.OwnershipToken), events,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task StoreCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        string checkpointId,
        string schemaVersion,
        string compatibilityKey,
        System.Text.Json.JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityKey);
        if (payload.GetRawText().Length > 1_048_576)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded,
                "A checkpoint payload cannot exceed 1 MiB.");
        var now = timeProvider.GetUtcNow();
        return assignments.StoreCheckpointAsync(proof, Digest(proof.OwnershipToken),
            new RuntimeAssignmentCheckpoint(checkpointId, schemaVersion, compatibilityKey, payload.Clone(), now), now, cancellationToken);
    }

    public Task<RuntimeAssignmentCheckpoint?> GetCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        string checkpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        return assignments.GetCheckpointAsync(proof, Digest(proof.OwnershipToken), checkpointId,
            timeProvider.GetUtcNow(), cancellationToken);
    }

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
