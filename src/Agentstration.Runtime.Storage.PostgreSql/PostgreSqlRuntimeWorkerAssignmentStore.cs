using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentstration.Runtime.Storage.PostgreSql;

internal sealed class RuntimeWorkerAssignmentDocument
{
    public Guid WorkspaceId { get; set; }
    public Guid AssignmentId { get; set; }
    public required string TargetKind { get; set; }
    public required string TargetRunId { get; set; }
    public required string RuntimeCapability { get; set; }
    public required string RuntimeCapabilityVersion { get; set; }
    public required string ExecutionMaterialVersion { get; set; }
    public required string State { get; set; }
    public long FencingGeneration { get; set; }
    public long? LeaseExpiresAt { get; set; }
    public Guid? ActiveWorkerId { get; set; }
    public Guid? ActiveWorkerSessionId { get; set; }
    public string? OwnershipTokenDigest { get; set; }
    public required string Payload { get; set; }
    public required string ETag { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal static class RuntimeWorkerAssignmentModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var assignment = modelBuilder.Entity<RuntimeWorkerAssignmentDocument>();
        assignment.ToTable("RuntimeWorkerAssignments");
        assignment.HasKey(value => new { value.WorkspaceId, value.AssignmentId });
        assignment.Property(value => value.TargetKind).HasMaxLength(32);
        assignment.Property(value => value.TargetRunId).HasMaxLength(64);
        assignment.Property(value => value.RuntimeCapability).HasMaxLength(128);
        assignment.Property(value => value.RuntimeCapabilityVersion).HasMaxLength(64);
        assignment.Property(value => value.ExecutionMaterialVersion).HasMaxLength(64);
        assignment.Property(value => value.State).HasMaxLength(32);
        assignment.Property(value => value.OwnershipTokenDigest).HasMaxLength(64);
        assignment.Property(value => value.ETag).HasMaxLength(64).IsConcurrencyToken();
        assignment.HasIndex(value => new { value.WorkspaceId, value.TargetKind, value.TargetRunId }).IsUnique();
        assignment.HasIndex(value => new { value.State, value.RuntimeCapability, value.CreatedAt });
        assignment.HasIndex(value => new { value.State, value.LeaseExpiresAt });
        assignment.HasIndex(value => new { value.State, value.ActiveWorkerId, value.ActiveWorkerSessionId });
    }
}

public sealed class PostgreSqlRuntimeWorkerAssignmentStore(
    IDbContextFactory<RuntimeRunDbContext> contextFactory) : IRuntimeWorkerAssignmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("The PostgreSQL Runtime assignment store is not accessible.");
    }

    public async Task<StoredRuntimeWorkerAssignment> CreateAsync(RuntimeWorkerAssignment assignment, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun
            && !await context.Runs.AnyAsync(value => value.WorkspaceId == assignment.WorkspaceId.Value
                && value.RunId == assignment.TargetRunId, cancellationToken))
            throw new RuntimeRunNotFoundException(assignment.TargetRunId);
        var etag = NewETag();
        context.WorkerAssignments.Add(ToDocument(assignment, etag, null));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new RuntimeRunConcurrencyException(exception.InnerException?.Message ?? exception.Message); }
        return new StoredRuntimeWorkerAssignment(assignment, etag);
    }

    public async Task<StoredRuntimeWorkerAssignment?> GetAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await context.WorkerAssignments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.WorkspaceId == workspaceId.Value && value.AssignmentId == assignmentId.Value, cancellationToken);
        return document is null ? null : Deserialize(document);
    }

    public async Task<StoredRuntimeWorkerAssignment?> GetByTargetAsync(
        WorkspaceId workspaceId, RuntimeAssignmentTargetKind targetKind, string targetRunId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var kind = targetKind.ToString();
        var document = await context.WorkerAssignments.AsNoTracking().SingleOrDefaultAsync(value =>
            value.WorkspaceId == workspaceId.Value && value.TargetKind == kind
            && value.TargetRunId == targetRunId, cancellationToken);
        return document is null ? null : Deserialize(document);
    }

    public async Task<IReadOnlyList<StoredRuntimeWorkerAssignment>> ListAsync(
        RuntimeWorkerAssignmentQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Skip < 0 || query.Take is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query), "Assignment query paging is invalid.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var documents = context.WorkerAssignments.AsNoTracking().AsQueryable();
        if (query.WorkspaceId is { } workspaceId)
            documents = documents.Where(value => value.WorkspaceId == workspaceId.Value);
        if (query.WorkerId is { } workerId)
            documents = documents.Where(value => value.ActiveWorkerId == workerId.Value);
        if (query.States is { Count: > 0 })
        {
            var states = query.States.Select(value => value.ToString()).ToArray();
            documents = documents.Where(value => states.Contains(value.State));
        }
        return (await documents.OrderByDescending(value => value.UpdatedAt)
            .Skip(query.Skip).Take(query.Take).ToArrayAsync(cancellationToken))
            .Select(Deserialize).ToArray();
    }

    public async Task<StoredRuntimeWorkerAssignment?> ClaimNextAsync(
        RuntimeWorkerClaimRequest request,
        byte[] ownershipTokenDigest,
        DateTimeOffset acquiredAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var activeCount = await context.WorkerAssignments.AsNoTracking()
            .Where(value => value.State == nameof(RuntimeAssignmentState.Assigned)
                && value.LeaseExpiresAt != null && value.LeaseExpiresAt > acquiredAt.UtcTicks
                && value.ActiveWorkerId == request.WorkerId.Value
                && value.ActiveWorkerSessionId == request.WorkerSessionId.Value)
            .CountAsync(cancellationToken);
        if (activeCount >= request.MaximumConcurrentAssignments) return null;
        var versions = request.RuntimeCapabilityVersions.ToArray();
        var materials = request.ExecutionMaterialVersions.ToArray();
        var document = await context.WorkerAssignments
            .Where(value => value.State == nameof(RuntimeAssignmentState.Pending)
                && value.RuntimeCapability == request.RuntimeCapability
                && versions.Contains(value.RuntimeCapabilityVersion)
                && materials.Contains(value.ExecutionMaterialVersion))
            .OrderBy(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null) return null;
        var assignment = Deserialize(document).Value;
        var generation = checked(assignment.FencingGeneration + 1);
        var attempt = new RuntimeAssignmentAttempt
        {
            Id = request.AttemptId,
            WorkerId = request.WorkerId,
            WorkerSessionId = request.WorkerSessionId,
            FencingGeneration = generation,
            AcquiredAt = acquiredAt,
            LastHeartbeatAt = acquiredAt,
            LeaseExpiresAt = leaseExpiresAt,
            State = RuntimeAssignmentAttemptState.Active
        };
        assignment = assignment with
        {
            State = RuntimeAssignmentState.Assigned,
            FencingGeneration = generation,
            UpdatedAt = acquiredAt,
            Attempts = assignment.Attempts.Append(attempt).ToArray()
        };
        Apply(document, assignment, Convert.ToHexString(ownershipTokenDigest), leaseExpiresAt);
        if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
            await SetRunStateAsync(context, assignment, RuntimeRunState.Running, acquiredAt, null, null, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) { return null; }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            return null;
        }
        return Deserialize(document);
    }

    public async Task<StoredRuntimeWorkerAssignment> RenewAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        DateTimeOffset renewedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, renewedAt);
        var current = assignment.CurrentAttempt! with { LastHeartbeatAt = renewedAt, LeaseExpiresAt = leaseExpiresAt };
        assignment = assignment with
        {
            UpdatedAt = renewedAt,
            Attempts = assignment.Attempts.Take(assignment.Attempts.Count - 1).Append(current).ToArray()
        };
        Apply(document, assignment, document.OwnershipTokenDigest, leaseExpiresAt);
        await SaveAsync(context, cancellationToken);
        return Deserialize(document);
    }

    public async Task<RuntimeAssignmentAuthorization> ValidateOwnershipAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        return new RuntimeAssignmentAuthorization(
            assignment,
            assignment.CancellationRequestedAt is not null,
            assignment.CurrentAttempt!.LeaseExpiresAt - observedAt);
    }

    public async Task<RuntimeAssignmentStepExecution> OpenStepExecutionAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentStepExecution step,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        var replay = assignment.StepExecutions.SingleOrDefault(value => value.CommandId == step.CommandId);
        if (replay is not null)
        {
            if (replay.FlowRunId != step.FlowRunId || replay.FlowVersion != step.FlowVersion
                || replay.FlowDefinitionHash != step.FlowDefinitionHash || replay.StepDefinitionId != step.StepDefinitionId)
                throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.ReplayConflict, "A replayed StepExecution command has different content.");
            return replay;
        }
        if (assignment.StepExecutions.Count >= 10_000)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded, "The assignment StepExecution limit was reached.");
        assignment = assignment with
        {
            UpdatedAt = observedAt,
            StepExecutions = assignment.StepExecutions.Append(step).ToArray()
        };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt!.LeaseExpiresAt);
        await SaveAsync(context, cancellationToken);
        return step;
    }

    public async Task<RuntimeAssignmentTurn> OpenTurnAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentTurn turn,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        var replay = assignment.Turns.SingleOrDefault(value => value.CommandId == turn.CommandId);
        if (replay is not null)
        {
            if (replay.RunId != turn.RunId || replay.StepExecutionId != turn.StepExecutionId
                || replay.ParticipantId != turn.ParticipantId)
                throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.ReplayConflict, "A replayed Turn command has different content.");
            return replay;
        }
        ValidateTurn(assignment, turn);
        if (assignment.Turns.Count >= 10_000)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded, "The assignment Turn limit was reached.");
        assignment = assignment with { UpdatedAt = observedAt, Turns = assignment.Turns.Append(turn).ToArray() };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt!.LeaseExpiresAt);
        await SaveAsync(context, cancellationToken);
        return turn;
    }

    public async Task RegisterChildFlowAsync(RuntimeAssignmentOwnershipProof proof, byte[] ownershipTokenDigest,
        string childFlowRunId, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        if (assignment.ChildFlowRunIds.Contains(childFlowRunId, StringComparer.Ordinal)) return;
        assignment = assignment with
        {
            UpdatedAt = observedAt,
            ChildFlowRunIds = assignment.ChildFlowRunIds.Append(childFlowRunId).ToArray()
        };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt!.LeaseExpiresAt);
        await SaveAsync(context, cancellationToken);
    }

    public async Task<RuntimeAssignmentEventAppendResult> AppendEventsAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (events.Count is < 1 or > 256)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded, "An event batch must contain between 1 and 256 events.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        var accepted = assignment.ExecutionEvents.ToList();
        var duplicates = new List<Guid>();
        var nextSequence = accepted.Count == 0 ? 1 : checked(accepted[^1].AttemptEventSequence + 1);
        foreach (var executionEvent in events)
        {
            var existing = accepted.FirstOrDefault(value => value.EventId == executionEvent.EventId);
            if (existing is not null)
            {
                if (!Equivalent(existing, executionEvent))
                    throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.ReplayConflict, "A replayed EventId has different content.");
                duplicates.Add(executionEvent.EventId);
                continue;
            }
            if (executionEvent.AttemptEventSequence != nextSequence)
                throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidEventSequence, $"Expected attempt event sequence {nextSequence}.");
            ValidateEventCoordinates(assignment, executionEvent);
            accepted.Add(executionEvent);
            nextSequence++;
        }
        if (accepted.Count > 10_000)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded, "The assignment event limit was reached.");
        assignment = assignment with { UpdatedAt = observedAt, ExecutionEvents = accepted };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt!.LeaseExpiresAt);
        await SaveAsync(context, cancellationToken);
        return new RuntimeAssignmentEventAppendResult(nextSequence - 1, duplicates);
    }

    public async Task StoreCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentCheckpoint checkpoint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        var existing = assignment.Checkpoints.SingleOrDefault(value => value.CheckpointId == checkpoint.CheckpointId);
        if (existing is not null && (!string.Equals(existing.SchemaVersion, checkpoint.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(existing.CompatibilityKey, checkpoint.CompatibilityKey, StringComparison.Ordinal)))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.ReplayConflict, "A checkpoint cannot be replaced with a different schema or compatibility key.");
        var checkpoints = assignment.Checkpoints.Where(value => value.CheckpointId != checkpoint.CheckpointId).Append(checkpoint).ToArray();
        if (checkpoints.Length > 100)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LimitExceeded, "The assignment checkpoint limit was reached.");
        assignment = assignment with { UpdatedAt = observedAt, Checkpoints = checkpoints };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt!.LeaseExpiresAt);
        await SaveAsync(context, cancellationToken);
    }

    public async Task<RuntimeAssignmentCheckpoint?> GetCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        string checkpointId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, observedAt);
        return assignment.Checkpoints.SingleOrDefault(value => value.CheckpointId == checkpointId);
    }

    public async Task<StoredRuntimeWorkerAssignment> RequestCancellationAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var document = await RequiredAsync(context, workspaceId, assignmentId, cancellationToken);
        var assignment = Deserialize(document).Value;
        if (assignment.State is RuntimeAssignmentState.Succeeded or RuntimeAssignmentState.Failed or RuntimeAssignmentState.Cancelled)
            return Deserialize(document);
        assignment = assignment with { CancellationRequestedAt = assignment.CancellationRequestedAt ?? requestedAt, UpdatedAt = requestedAt };
        if (assignment.State == RuntimeAssignmentState.Pending)
            assignment = assignment with { State = RuntimeAssignmentState.Cancelled };
        Apply(document, assignment, document.OwnershipTokenDigest, assignment.CurrentAttempt?.LeaseExpiresAt);
        if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
            await SetRunStateAsync(context, assignment, RuntimeRunState.Cancelled, requestedAt, null, "Cancelled by the caller.", cancellationToken);
        await SaveAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Deserialize(document);
    }

    public async Task<StoredRuntimeWorkerAssignment> RequeueAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await RequiredAsync(context, workspaceId, assignmentId, cancellationToken);
        var assignment = Deserialize(document).Value;
        if (assignment.State != RuntimeAssignmentState.Succeeded
            || assignment.CurrentAttempt?.State != RuntimeAssignmentAttemptState.Succeeded)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.TerminalConflict,
                "Only a successfully suspended assignment can be requeued.");
        assignment = assignment with
        {
            State = RuntimeAssignmentState.Pending,
            UpdatedAt = requestedAt,
            CancellationRequestedAt = null
        };
        Apply(document, assignment, null, null);
        await SaveAsync(context, cancellationToken);
        return Deserialize(document);
    }

    public async Task<RuntimeAssignmentTerminalResult> CompleteAsync(
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        RuntimeAssignmentTerminalCommand command,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var document = await RequiredAsync(context, proof.WorkspaceId, proof.AssignmentId, cancellationToken);
        var existing = Deserialize(document).Value;
        if (existing.CurrentAttempt?.TerminalEventId == command.EventId)
        {
            ValidateTerminalReplay(document, proof, ownershipTokenDigest);
            return new RuntimeAssignmentTerminalResult(existing, ToRunState(existing.State), true);
        }
        if (existing.State is RuntimeAssignmentState.Succeeded or RuntimeAssignmentState.Failed or RuntimeAssignmentState.Cancelled)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.TerminalConflict, "The assignment already has a different terminal outcome.");
        var assignment = ValidateOwnership(document, proof, ownershipTokenDigest, completedAt);
        var run = assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun
            ? await RequiredRunAsync(context, assignment, cancellationToken)
            : null;
        var cancelled = assignment.CancellationRequestedAt is not null || run?.State == nameof(RuntimeRunState.Cancelled);
        var assignmentState = cancelled ? RuntimeAssignmentState.Cancelled : command.Outcome == RuntimeAssignmentTerminalOutcome.Succeeded
            ? RuntimeAssignmentState.Succeeded : RuntimeAssignmentState.Failed;
        var attemptState = cancelled ? RuntimeAssignmentAttemptState.Interrupted : command.Outcome == RuntimeAssignmentTerminalOutcome.Succeeded
            ? RuntimeAssignmentAttemptState.Succeeded : RuntimeAssignmentAttemptState.Failed;
        var current = assignment.CurrentAttempt! with
        {
            State = attemptState,
            CompletedAt = completedAt,
            TerminalEventId = command.EventId,
            ErrorCode = cancelled ? null : command.ErrorCode
        };
        assignment = assignment with
        {
            State = assignmentState,
            UpdatedAt = completedAt,
            Attempts = assignment.Attempts.Take(assignment.Attempts.Count - 1).Append(current).ToArray()
        };
        Apply(document, assignment, document.OwnershipTokenDigest, null);
        var runState = ToRunState(assignmentState);
        if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
        {
            await SetRunStateAsync(context, assignment, runState, completedAt,
                cancelled ? null : command.Response,
                cancelled ? "Cancelled by the caller." : command.Error,
                cancellationToken,
                cancelled ? null : command.ErrorCode);
            await AppendTerminalEventAsync(context, assignment, command.EventId, runState, completedAt,
                cancelled ? "Run cancelled" : command.Error ?? "Run completed", cancellationToken);
        }
        await SaveAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RuntimeAssignmentTerminalResult(assignment, runState, false);
    }

    public async Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> ExpireLeasesAsync(
        DateTimeOffset observedAt,
        int take,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var documents = await context.WorkerAssignments
            .Where(value => value.State == nameof(RuntimeAssignmentState.Assigned)
                && value.LeaseExpiresAt != null && value.LeaseExpiresAt <= observedAt.UtcTicks)
            .OrderBy(value => value.LeaseExpiresAt).Take(take).ToArrayAsync(cancellationToken);
        var results = new List<RuntimeAssignmentTerminalResult>(documents.Length);
        foreach (var document in documents)
        {
            var assignment = Deserialize(document).Value;
            var cancelled = assignment.CancellationRequestedAt is not null;
            var current = assignment.CurrentAttempt! with
            {
                State = RuntimeAssignmentAttemptState.Interrupted,
                CompletedAt = observedAt,
                ErrorCode = cancelled ? null : "worker_lost"
            };
            assignment = assignment with
            {
                State = cancelled ? RuntimeAssignmentState.Cancelled : RuntimeAssignmentState.Failed,
                UpdatedAt = observedAt,
                Attempts = assignment.Attempts.Take(assignment.Attempts.Count - 1).Append(current).ToArray()
            };
            Apply(document, assignment, null, null);
            var runState = cancelled ? RuntimeRunState.Cancelled : RuntimeRunState.Failed;
            if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
            {
                await SetRunStateAsync(context, assignment, runState, observedAt, null,
                    cancelled ? "Cancelled by the caller." : "The Runtime Worker lease expired.", cancellationToken,
                    cancelled ? null : "worker_lost");
                await AppendTerminalEventAsync(context, assignment, Guid.NewGuid(), runState, observedAt,
                    cancelled ? "Run cancelled after Worker loss" : "Runtime Worker lost", cancellationToken);
            }
            results.Add(new RuntimeAssignmentTerminalResult(assignment, runState, false));
        }
        await SaveAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<IReadOnlyList<RuntimeAssignmentTerminalResult>> InterruptSupersededSessionsAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId activeSessionId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var documents = await context.WorkerAssignments
            .Where(value => value.State == nameof(RuntimeAssignmentState.Assigned)
                && value.ActiveWorkerId == workerId.Value
                && value.ActiveWorkerSessionId != activeSessionId.Value)
            .ToArrayAsync(cancellationToken);
        var results = new List<RuntimeAssignmentTerminalResult>();
        foreach (var document in documents)
        {
            var assignment = Deserialize(document).Value;
            var attempt = assignment.CurrentAttempt;
            if (attempt?.WorkerId != workerId || attempt.WorkerSessionId == activeSessionId) continue;
            var cancelled = assignment.CancellationRequestedAt is not null;
            var interrupted = attempt with
            {
                State = RuntimeAssignmentAttemptState.Interrupted,
                CompletedAt = observedAt,
                ErrorCode = cancelled ? null : "worker_lost"
            };
            assignment = assignment with
            {
                State = cancelled ? RuntimeAssignmentState.Cancelled : RuntimeAssignmentState.Failed,
                UpdatedAt = observedAt,
                Attempts = assignment.Attempts.Take(assignment.Attempts.Count - 1).Append(interrupted).ToArray()
            };
            Apply(document, assignment, null, null);
            var runState = cancelled ? RuntimeRunState.Cancelled : RuntimeRunState.Failed;
            if (assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
            {
                await SetRunStateAsync(context, assignment, runState, observedAt, null,
                    cancelled ? "Cancelled by the caller." : "The Runtime Worker session was superseded.", cancellationToken,
                    cancelled ? null : "worker_lost");
                await AppendTerminalEventAsync(context, assignment, Guid.NewGuid(), runState, observedAt,
                    cancelled ? "Run cancelled after Worker session replacement" : "Runtime Worker session superseded", cancellationToken);
            }
            results.Add(new RuntimeAssignmentTerminalResult(assignment, runState, false));
        }
        await SaveAsync(context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private static RuntimeWorkerAssignment ValidateOwnership(
        RuntimeWorkerAssignmentDocument document,
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest,
        DateTimeOffset observedAt)
    {
        var assignment = Deserialize(document).Value;
        var attempt = assignment.CurrentAttempt;
        if (assignment.State != RuntimeAssignmentState.Assigned || attempt is null || attempt.State != RuntimeAssignmentAttemptState.Active
            || attempt.Id != proof.AttemptId || attempt.WorkerId != proof.WorkerId || attempt.WorkerSessionId != proof.WorkerSessionId)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned, "The assignment is not owned by this Worker session and attempt.");
        if (attempt.FencingGeneration != proof.FencingGeneration || assignment.FencingGeneration != proof.FencingGeneration)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.FencingRejected, "The assignment fencing generation is no longer current.");
        if (attempt.LeaseExpiresAt <= observedAt)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LeaseExpired, "The assignment lease has expired.");
        var storedDigest = document.OwnershipTokenDigest is null ? [] : Convert.FromHexString(document.OwnershipTokenDigest);
        if (storedDigest.Length != ownershipTokenDigest.Length || !CryptographicOperations.FixedTimeEquals(storedDigest, ownershipTokenDigest))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned, "The ownership token is invalid.");
        return assignment;
    }

    private static void ValidateTurn(RuntimeWorkerAssignment assignment, RuntimeAssignmentTurn turn)
    {
        var step = turn.StepExecutionId is { } stepId
            ? assignment.StepExecutions.SingleOrDefault(value => value.Id == stepId)
            : null;
        var expectedRunId = step?.FlowRunId ?? assignment.TargetRunId;
        if (turn.AttemptNumber != 1 || turn.AttemptId == Guid.Empty || turn.Id == Guid.Empty
            || !string.Equals(turn.RunId, expectedRunId, StringComparison.Ordinal))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The Turn coordinate is invalid for this assignment.");
        if (turn.StepExecutionId is { } referencedStepId && assignment.StepExecutions.All(value => value.Id != referencedStepId))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The Turn references an unknown StepExecution.");
    }

    private static void ValidateEventCoordinates(RuntimeWorkerAssignment assignment, RuntimeAssignmentExecutionEvent executionEvent)
    {
        var step = executionEvent.StepExecutionId is { } eventStepId
            ? assignment.StepExecutions.SingleOrDefault(value => value.Id == eventStepId)
            : null;
        var validRun = step is not null
            ? string.Equals(executionEvent.RunId, step.FlowRunId, StringComparison.Ordinal)
            : string.Equals(executionEvent.RunId, assignment.TargetRunId, StringComparison.Ordinal)
              || assignment.ChildFlowRunIds.Contains(executionEvent.RunId, StringComparer.Ordinal);
        if (executionEvent.EventId == Guid.Empty || executionEvent.AttemptEventSequence < 1
            || !validRun)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The execution event identity is invalid.");
        if (executionEvent.StepExecutionId is { } stepId && assignment.StepExecutions.All(value => value.Id != stepId))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The event references an unknown StepExecution.");
        if (executionEvent.TurnId is not { } turnId) return;
        var turn = assignment.Turns.SingleOrDefault(value => value.Id == turnId)
            ?? throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The event references an unknown Turn.");
        if (executionEvent.TurnAttemptId != turn.AttemptId)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The event references an unknown TurnAttempt.");
        if (executionEvent.StepExecutionId != turn.StepExecutionId)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.InvalidCoordinate, "The event Turn and StepExecution do not belong to the same coordinate.");
    }

    private static bool Equivalent(RuntimeAssignmentExecutionEvent left, RuntimeAssignmentExecutionEvent right) =>
        left.EventId == right.EventId
        && left.AttemptEventSequence == right.AttemptEventSequence
        && left.OccurredAt == right.OccurredAt
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && left.StepExecutionId == right.StepExecutionId
        && left.TurnId == right.TurnId
        && left.TurnAttemptId == right.TurnAttemptId
        && left.ToolCallId == right.ToolCallId
        && NullableJsonEquals(left.Payload, right.Payload);

    private static bool NullableJsonEquals(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || JsonElement.DeepEquals(left.Value, right!.Value));

    private static void ValidateTerminalReplay(
        RuntimeWorkerAssignmentDocument document,
        RuntimeAssignmentOwnershipProof proof,
        byte[] ownershipTokenDigest)
    {
        var attempt = Deserialize(document).Value.CurrentAttempt;
        var storedDigest = document.OwnershipTokenDigest is null ? [] : Convert.FromHexString(document.OwnershipTokenDigest);
        if (attempt is null || attempt.Id != proof.AttemptId || attempt.WorkerId != proof.WorkerId
            || attempt.WorkerSessionId != proof.WorkerSessionId || attempt.FencingGeneration != proof.FencingGeneration
            || storedDigest.Length != ownershipTokenDigest.Length
            || !CryptographicOperations.FixedTimeEquals(storedDigest, ownershipTokenDigest))
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned, "The terminal command does not belong to this assignment owner.");
    }

    private static async Task<RuntimeWorkerAssignmentDocument> RequiredAsync(
        RuntimeRunDbContext context,
        WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        CancellationToken cancellationToken) =>
        await context.WorkerAssignments.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId.Value
            && value.AssignmentId == assignmentId.Value, cancellationToken)
        ?? throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned, "The assignment was not found.");

    private static async Task<RuntimeRunDocument> RequiredRunAsync(
        RuntimeRunDbContext context,
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken) =>
        await context.Runs.SingleOrDefaultAsync(value => value.WorkspaceId == assignment.WorkspaceId.Value
            && value.RunId == assignment.TargetRunId, cancellationToken)
        ?? throw new RuntimeRunNotFoundException(assignment.TargetRunId);

    private static async Task SetRunStateAsync(
        RuntimeRunDbContext context,
        RuntimeWorkerAssignment assignment,
        RuntimeRunState state,
        DateTimeOffset now,
        string? response,
        string? error,
        CancellationToken cancellationToken,
        string? errorCode = null)
    {
        var document = await RequiredRunAsync(context, assignment, cancellationToken);
        var run = JsonSerializer.Deserialize<RuntimeRun>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored runtime run '{document.RunId}' is invalid.");
        if (run.Status.State.IsTerminal() && run.Status.State != RuntimeRunState.Cancelled) return;
        var status = run.Status with
        {
            State = state,
            StartedAt = state == RuntimeRunState.Running ? run.Status.StartedAt ?? now : run.Status.StartedAt,
            CompletedAt = state.IsTerminal() ? now : null,
            Response = response ?? run.Status.Response,
            Error = error,
            ErrorCode = errorCode
        };
        var etag = NewETag();
        document.State = state.ToString();
        document.ETag = etag;
        document.UpdatedAt = now.UtcTicks;
        document.Payload = JsonSerializer.Serialize(run with { Status = status, ETag = etag }, JsonOptions);
    }

    private static async Task AppendTerminalEventAsync(
        RuntimeRunDbContext context,
        RuntimeWorkerAssignment assignment,
        Guid eventId,
        RuntimeRunState state,
        DateTimeOffset timestamp,
        string message,
        CancellationToken cancellationToken)
    {
        var sequence = (await context.Events.Where(value => value.WorkspaceId == assignment.WorkspaceId.Value
            && value.RunId == assignment.TargetRunId).MaxAsync(value => (long?)value.Sequence, cancellationToken) ?? 0) + 1;
        var runEvent = new RuntimeRunEvent
        {
            WorkspaceId = assignment.WorkspaceId,
            Sequence = sequence,
            EventId = eventId,
            RunId = assignment.TargetRunId,
            Kind = RuntimeRunEventKind.RunCompleted,
            Timestamp = timestamp,
            Message = message,
            State = state
        };
        context.Events.Add(new RuntimeRunEventDocument
        {
            WorkspaceId = assignment.WorkspaceId.Value,
            RunId = assignment.TargetRunId,
            Sequence = sequence,
            Payload = JsonSerializer.Serialize(runEvent, JsonOptions),
            Timestamp = timestamp
        });
    }

    private static void Apply(RuntimeWorkerAssignmentDocument document, RuntimeWorkerAssignment assignment, string? digest, DateTimeOffset? leaseExpiresAt)
    {
        var etag = NewETag();
        document.State = assignment.State.ToString();
        document.FencingGeneration = assignment.FencingGeneration;
        document.LeaseExpiresAt = leaseExpiresAt?.UtcTicks;
        var activeAttempt = assignment.State == RuntimeAssignmentState.Assigned ? assignment.CurrentAttempt : null;
        document.ActiveWorkerId = activeAttempt?.WorkerId.Value;
        document.ActiveWorkerSessionId = activeAttempt?.WorkerSessionId.Value;
        document.OwnershipTokenDigest = digest;
        document.Payload = JsonSerializer.Serialize(assignment, JsonOptions);
        document.ETag = etag;
        document.UpdatedAt = assignment.UpdatedAt.UtcTicks;
    }

    private static RuntimeWorkerAssignmentDocument ToDocument(RuntimeWorkerAssignment assignment, string etag, string? digest) => new()
    {
        WorkspaceId = assignment.WorkspaceId.Value,
        AssignmentId = assignment.Id.Value,
        TargetKind = assignment.TargetKind.ToString(),
        TargetRunId = assignment.TargetRunId,
        RuntimeCapability = assignment.RuntimeCapability,
        RuntimeCapabilityVersion = assignment.RuntimeCapabilityVersion,
        ExecutionMaterialVersion = assignment.ExecutionMaterialVersion,
        State = assignment.State.ToString(),
        FencingGeneration = assignment.FencingGeneration,
        LeaseExpiresAt = assignment.CurrentAttempt?.LeaseExpiresAt.UtcTicks,
        ActiveWorkerId = assignment.State == RuntimeAssignmentState.Assigned ? assignment.CurrentAttempt?.WorkerId.Value : null,
        ActiveWorkerSessionId = assignment.State == RuntimeAssignmentState.Assigned ? assignment.CurrentAttempt?.WorkerSessionId.Value : null,
        OwnershipTokenDigest = digest,
        Payload = JsonSerializer.Serialize(assignment, JsonOptions),
        ETag = etag,
        CreatedAt = assignment.CreatedAt.UtcTicks,
        UpdatedAt = assignment.UpdatedAt.UtcTicks
    };

    private static StoredRuntimeWorkerAssignment Deserialize(RuntimeWorkerAssignmentDocument document)
    {
        var assignment = JsonSerializer.Deserialize<RuntimeWorkerAssignment>(document.Payload, JsonOptions)
            ?? throw new InvalidOperationException($"Stored Runtime Worker assignment '{document.AssignmentId}' is invalid.");
        return new StoredRuntimeWorkerAssignment(assignment, document.ETag);
    }

    private static RuntimeRunState ToRunState(RuntimeAssignmentState state) => state switch
    {
        RuntimeAssignmentState.Succeeded => RuntimeRunState.Succeeded,
        RuntimeAssignmentState.Cancelled => RuntimeRunState.Cancelled,
        RuntimeAssignmentState.Failed => RuntimeRunState.Failed,
        _ => RuntimeRunState.Running
    };

    private static async Task SaveAsync(RuntimeRunDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new RuntimeRunConcurrencyException(exception.Message); }
    }

    private static string NewETag() => $"\"{Guid.NewGuid():N}\"";
}
