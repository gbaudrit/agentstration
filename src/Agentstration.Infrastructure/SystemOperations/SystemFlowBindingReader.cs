using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;

namespace Agentstration.Infrastructure.SystemOperations;

/// <summary>Correlates recorded native bindings and ledger coordinates without resolving execution material.</summary>
public sealed class SystemFlowBindingReader(FlowRunService flows, IRuntimeWorkerAssignmentStore assignments)
{
    public async Task<(IReadOnlyList<SystemFlowBindingEvidence> Items, bool HasMore)> ReadAsync(SystemGraphNode root,
        Guid agentUid, SystemOperationsReadContext context, DateTimeOffset start, int take, IReadOnlyDictionary<Guid, string> presence, CancellationToken ct)
    {
        var query = new Agentstration.Flows.Storage.Abstractions.FlowRunAgentBindingQuery(SystemOperationsAccess.FlowScope(context),
            root.Identity.OwnerScope, root.Identity.Namespace, agentUid, root.Identity.Kind == "AgentRevision" ? root.Identity.ResourceUid : null,
            root.State == "recorded-generation" ? root.Evidence?.Generation : null, root.Identity.Kind == "AgentRevision" ? root.Evidence?.Hash : null, start, take);
        var page = await flows.ListByAgentBindingAsync(query, ct);
        var candidates = page.Items.Where(stored => stored.Value.Scope.PrincipalId == context.ActorId
                && stored.Value.Scope.TenantId == context.TenantId && stored.Value.ExecutionScopeRef == context.DataScope
                && stored.Value.Scope.AccessScopeRef == context.InvocationScope && stored.Value.Id.Length is > 0 and <= 160)
            .SelectMany(stored => stored.Value.RuntimeBindings.Where(binding => Matches(binding, query))
                .DistinctBy(binding => (binding.ParticipantId, binding.AgentUid, binding.RevisionUid))
                .Select(binding => (Run: stored.Value, Binding: binding)))
            .Take(take + 1).ToArray();
        var result = new List<SystemFlowBindingEvidence>();
        var cached = new Dictionary<string, RuntimeWorkerAssignment?>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Take(take))
        {
            var run = candidate.Run; var binding = candidate.Binding;
            var rootRunId = run.RootFlowRunId ?? run.Id;
            if (!cached.TryGetValue(rootRunId, out var assignment))
            {
                assignment = (await assignments.GetByTargetExactAsync(context.DataScope, RuntimeAssignmentTargetKind.FlowRun, rootRunId, ct))?.Value;
                cached.Add(rootRunId, assignment);
            }
            var invocations = ProjectInvocations(run, binding.ParticipantId, assignment, 9);
            result.Add(new(run.Id, run.Scope.AccessScopeRef, run.ExecutionScopeRef, binding.AgentScopeRef!.Value,
                binding.AgentNamespace, binding.AgentUid, binding.RevisionUid, binding.AgentGeneration, binding.DefinitionHash,
                binding.ParticipantId, RunState(run.Status), run.CreatedAt, invocations.Take(8).Select(invocation => invocation with { Presence = presence.GetValueOrDefault(invocation.WorkerId, "unknown") }).ToArray())
            { MoreInvocations = invocations.Count > 8 });
        }
        return (result, page.HasMore || candidates.Length > take);
    }
    private static bool Matches(RuntimeExecutionBinding binding, Agentstration.Flows.Storage.Abstractions.FlowRunAgentBindingQuery query) =>
        binding.AgentUid == query.AgentUid && binding.AgentScopeRef == query.AgentOwner && binding.AgentNamespace == query.AgentNamespace
        && binding.RevisionUid != Guid.Empty && binding.AgentGeneration > 0 && binding.ParticipantId.Length is > 0 and <= 160
        && (query.RevisionUid is null || binding.RevisionUid == query.RevisionUid)
        && (query.Generation is null || binding.AgentGeneration == query.Generation)
        && (query.DefinitionHash is null || binding.DefinitionHash == query.DefinitionHash);

    public static IReadOnlyList<SystemFlowInvocationEvidence> ProjectInvocations(FlowRun run, string participant, RuntimeWorkerAssignment? assignment, int take = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 9);
        if (assignment is null || assignment.ExecutionScopeRef != run.ExecutionScopeRef || assignment.TenantId != run.Scope.TenantId
            || assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun || assignment.TargetRunId != (run.RootFlowRunId ?? run.Id))
            return [];
        // A participant with conflicting recorded identities does not prove which Agent executed.
        var identities = run.RuntimeBindings.Where(binding => binding.ParticipantId == participant)
            .Select(binding => (binding.AgentUid, binding.RevisionUid, binding.AgentNamespace, binding.AgentScopeRef, binding.AgentGeneration, binding.DefinitionHash))
            .Distinct().Take(2).Count();
        if (identities != 1) return [];
        var result = new List<SystemFlowInvocationEvidence>();
        foreach (var group in assignment.Turns.Where(turn => turn.RunId == run.Id && turn.ParticipantId == participant)
            .DistinctBy(turn => turn.Id).GroupBy(turn => (turn.AttemptId, turn.StepExecutionId))
            .OrderByDescending(turns => turns.Max(turn => turn.OpenedAt))
            .ThenByDescending(turns => assignment.Attempts.FirstOrDefault(attempt => attempt.Id.Value == turns.Key.AttemptId)?.FencingGeneration ?? -1)
            .ThenBy(turns => turns.Key.AttemptId))
        {
            var attempt = assignment.Attempts.FirstOrDefault(attempt => attempt.Id.Value == group.Key.AttemptId);
            if (attempt is null) continue;
            var step = group.Key.StepExecutionId is { } stepId ? assignment.StepExecutions.FirstOrDefault(step => step.Id == stepId) : null;
            if (group.Key.StepExecutionId is not null && (step is null || step.FlowRunId != run.Id || step.FlowVersion != run.FlowVersion
                || run.DefinitionHash is null || step.FlowDefinitionHash != run.DefinitionHash || step.StepDefinitionId.Length > 160)) continue;
            result.Add(new(assignment.Id.Value, attempt.Id.Value, step?.Id, step?.StepDefinitionId, attempt.WorkerId.Value,
                attempt.WorkerSessionId.Value, attempt.FencingGeneration, attempt.State.ToString().ToLowerInvariant(),
                group.Count(), group.Min(turn => turn.OpenedAt), group.Max(turn => turn.OpenedAt)));
            if (result.Count == take) break;
        }
        return result;
    }
    private static string RunState(FlowRunStatus state) => state switch
    { FlowRunStatus.Succeeded => "succeeded", FlowRunStatus.Failed => "failed", FlowRunStatus.Cancelled => "cancelled", FlowRunStatus.TimedOut => "timed-out", FlowRunStatus.Running => "running", FlowRunStatus.WaitingForInput or FlowRunStatus.WaitingForChild => "waiting", _ => "pending" };
}
