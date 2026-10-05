using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeObservabilityQueryService(
    AwpWorkerIdentityService identities,
    RuntimeWorkerDispatchService dispatch,
    IRuntimeWorkerAssignmentStore assignments,
    IRuntimeExecutionMaterialResolver materials,
    RuntimeWorkerLeaseOptions leaseOptions,
    TimeProvider timeProvider) : IRuntimeObservabilityQueryService
{
    public async Task<IReadOnlyList<RuntimeWorkerSummaryResponse>> ListWorkersAsync(CancellationToken cancellationToken)
    {
        var enrolled = await identities.ListAsync(cancellationToken);
        var registrations = dispatch.ListRegistrations().ToDictionary(value => value.WorkerId.Value);
        var active = await assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            States = new HashSet<RuntimeAssignmentState> { RuntimeAssignmentState.Assigned },
            Take = 500
        }, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return enrolled.Select(identity =>
        {
            registrations.TryGetValue(identity.WorkerId, out var registration);
            var activeCount = active.Count(value => value.Value.CurrentAttempt is { State: RuntimeAssignmentAttemptState.Active } attempt
                && attempt.WorkerId.Value == identity.WorkerId && attempt.LeaseExpiresAt > now);
            return Map(identity, registration, activeCount, now);
        }).OrderBy(value => value.DisplayName, StringComparer.Ordinal).ToArray();
    }

    public async Task<RuntimeWorkerDetailsResponse?> GetWorkerAsync(Guid workerId, CancellationToken cancellationToken)
    {
        var worker = (await ListWorkersAsync(cancellationToken)).SingleOrDefault(value => value.WorkerId == workerId);
        if (worker is null) return null;
        var recent = await assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            WorkerId = new RuntimeWorkerId(workerId),
            Take = 100
        }, cancellationToken);
        return new(worker, recent.Select(value => Map(value.Value)).ToArray());
    }

    public async Task<IReadOnlyList<AgentInstanceResponse>> ListAgentInstancesAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var active = await assignments.ListAsync(new RuntimeWorkerAssignmentQuery
        {
            WorkspaceId = workspaceId,
            States = new HashSet<RuntimeAssignmentState> { RuntimeAssignmentState.Assigned },
            Take = 500
        }, cancellationToken);
        var result = new List<AgentInstanceResponse>();
        foreach (var stored in active)
        {
            var assignment = stored.Value;
            var attempt = assignment.CurrentAttempt;
            if (attempt is not { State: RuntimeAssignmentAttemptState.Active } || attempt.LeaseExpiresAt <= now)
                continue;
            var material = await materials.ResolveAsync(assignment, cancellationToken);
            var agents = material switch
            {
                RuntimeDirectAgentExecutionMaterial direct => new[] { direct.Agent },
                RuntimeRootFlowExecutionMaterial flow => flow.Agents,
                _ => []
            };
            foreach (var turns in assignment.Turns
                .Where(value => !string.IsNullOrWhiteSpace(value.ParticipantId))
                .GroupBy(value => new { value.ParticipantId, value.StepExecutionId }))
            {
                var agent = agents.SingleOrDefault(value => string.Equals(value.ParticipantId, turns.Key.ParticipantId, StringComparison.Ordinal));
                if (agent is null) continue;
                var turnIds = turns.Select(value => value.Id).ToHashSet();
                var activity = assignment.ExecutionEvents
                    .Where(value => value.TurnId is { } turnId && turnIds.Contains(turnId))
                    .Select(value => value.OccurredAt)
                    .Append(turns.Max(value => value.OpenedAt))
                    .Max();
                var started = turns.Min(value => value.OpenedAt);
                var instanceId = $"{assignment.Id.Value:N}:{attempt.Id.Value:N}:{turns.Key.ParticipantId}:{turns.Key.StepExecutionId?.ToString("N") ?? "direct"}";
                result.Add(new(instanceId, agent.AgentNamespace, agent.AgentName, agent.Generation,
                    agent.RevisionId, agent.RuntimeProfileName, assignment.TargetKind.ToString(), assignment.TargetRunId,
                    turns.Key.ParticipantId, turns.Key.StepExecutionId, turns.Count(), assignment.Id.Value,
                    attempt.Id.Value, attempt.WorkerId.Value, attempt.WorkerSessionId.Value, started, activity,
                    attempt.LeaseExpiresAt));
            }
        }
        return result.OrderByDescending(value => value.StartedAt).ToArray();
    }

    public async Task<RuntimeAssignmentPlacementResponse?> GetPlacementAsync(
        WorkspaceId workspaceId,
        RuntimeAssignmentTargetKind targetKind,
        string runId,
        CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetByTargetAsync(workspaceId, targetKind, runId, cancellationToken);
        return assignment is null ? null : Map(assignment.Value);
    }

    private RuntimeWorkerSummaryResponse Map(
        AwpWorkerEnrollmentView identity,
        RuntimeWorkerRegistration? registration,
        int activeAssignments,
        DateTimeOffset now)
    {
        var activeRegistration = registration is not null
            && registration.WorkerSessionId.Value == identity.ActiveSessionId
                ? registration
                : null;
        var presence = identity.State != AwpWorkerEnrollmentState.Active
            ? RuntimeWorkerPresenceState.Offline
            : activeRegistration is null
                ? RuntimeWorkerPresenceState.Unknown
                : now - activeRegistration.LastSeenAt <= leaseOptions.LeaseDuration
                    ? RuntimeWorkerPresenceState.Online
                    : now - activeRegistration.LastSeenAt <= leaseOptions.LeaseDuration * 2
                        ? RuntimeWorkerPresenceState.Stale
                        : RuntimeWorkerPresenceState.Offline;
        return new(identity.WorkerId, identity.DisplayName, identity.ProtocolVersion, identity.State.ToString(),
            presence, activeRegistration?.WorkerSessionId.Value ?? identity.ActiveSessionId, activeRegistration?.SoftwareVersion,
            activeRegistration?.MaximumConcurrentAssignments, activeAssignments, activeRegistration?.RegisteredAt,
            activeRegistration?.LastSeenAt, activeRegistration?.Capabilities.Select(value => new RuntimeWorkerCapabilityResponse(
                value.RuntimeKind, value.CapabilityVersion, value.ExecutionMaterialVersions.Order().ToArray(),
                value.RuntimeImplementationVersion)).ToArray() ?? []);
    }

    private static RuntimeAssignmentPlacementResponse Map(RuntimeWorkerAssignment assignment) => new(
        assignment.Id.Value, assignment.TargetKind.ToString(), assignment.TargetRunId, assignment.State.ToString(),
        assignment.RuntimeCapability, assignment.RuntimeCapabilityVersion, assignment.ExecutionMaterialVersion,
        assignment.CreatedAt, assignment.UpdatedAt, assignment.Attempts.Select(attempt => new RuntimeAssignmentAttemptResponse(
            attempt.Id.Value, attempt.WorkerId.Value, attempt.WorkerSessionId.Value, attempt.FencingGeneration,
            attempt.State.ToString(), attempt.AcquiredAt, attempt.LastHeartbeatAt, attempt.LeaseExpiresAt,
            attempt.CompletedAt, attempt.ErrorCode)).ToArray());
}
