using System.Globalization;
using Agentstration.Agents;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Work;

namespace Agentstration.Infrastructure.SystemOperations;

public sealed class SystemOperationsEvidenceReader(RuntimeRunService runtime, FlowRunService flows,
    IRuntimeObservabilityQueryService placement, IResourceIdentityCatalog identities, IResourceStore resources,
    ISystemGraphAuthorization graphAuthorization, ISystemGraphReadScope metadataScope,
    WorkItemService work, IFlowRunExecutionScope flowScopes) : ISystemOperationsEvidenceReader
{
    public async Task<SystemRunEvidencePage> ReadAsync(SystemGraphNode root, SystemOperationsReadContext context,
        DateTimeOffset windowStart, int take, CancellationToken ct)
    {
        if (!context.CanReadRuns) return new([], false, "run-read-required");
        var result = new List<SystemRunEvidence>(); bool more;
        var presence = context.CanInspectWorkers
            ? (await placement.ListWorkersAsync(ct)).ToDictionary(worker => worker.WorkerId, worker => worker.Presence.ToString().ToLowerInvariant())
            : new Dictionary<Guid, string>();
        if (root.Identity.Kind == "Flow")
        {
            var page = await flows.ListByIdentityAsync(new(SystemOperationsAccess.FlowScope(context), root.Identity.OwnerScope,
                root.Identity.Namespace, root.Identity.ResourceUid, root.State == "publication" ? root.Evidence?.Publication : null, windowStart, take, root.State == "publication" ? root.Evidence?.Hash : null), ct);
            more = page.HasMore;
            foreach (var stored in page.Items)
            {
                var run = stored.Value;
                if (run.DefinitionSnapshot.ResourceUid is not { } uid || run.Id.Length > 160) continue;
                var link = await WorkLinkAsync(run, context, ct);
                var usage = run.TokenUsage.Direct;
                result.Add(new("flow", run.Id, run.Scope.AccessScopeRef, run.ExecutionScopeRef, run.DefinitionSnapshot.ScopeRef, run.FlowId.Namespace,
                    uid, null, run.FlowVersion, run.DefinitionSnapshot.DefinitionHash, State(run.Status), run.CreatedAt, run.StartedAt, run.CompletedAt,
                    usage.InputTokens, usage.OutputTokens, usage.Coverage.ToString().ToLowerInvariant(),
                    await PlacementAsync(context, RuntimeAssignmentTargetKind.FlowRun, run.Id, presence, ct), link));
            }
        }
        else
        {
            var agentUid = root.Identity.ResourceUid;
            if (root.Identity.Kind == "AgentRevision")
            {
                using var trusted = metadataScope.Enter();
                var revision = await resources.GetExactAsync<AgentRevision>(new(root.Identity.OwnerScope, root.Identity.Namespace, root.Identity.Kind, root.Identity.Name), ct);
                if (revision?.Value.Uid != root.Identity.ResourceUid || revision.Value.AgentUid == Guid.Empty)
                    return new([], false, "unavailable");
                agentUid = revision.Value.AgentUid;
            }
            var query = new RuntimeRunIdentityQuery(context.DataScope, root.Identity.OwnerScope, root.Identity.Namespace,
                agentUid, root.Identity.Kind == "AgentRevision" ? root.Identity.ResourceUid : null,
                root.State == "recorded-generation" ? root.Evidence?.Generation : null, windowStart, take + 1, context.InvocationScope, root.Identity.Kind == "AgentRevision" ? root.Evidence?.Hash : null);
            var runs = await runtime.ListByIdentityAsync(SystemOperationsAccess.RuntimeScope(context), query, ct);
            more = runs.Count > take;
            foreach (var stored in runs.Take(take))
            {
                var run = stored.Value; var agent = run.Properties.Agent;
                if (agent.ResourceUid is not { } uid || agent.ScopeRef is not { } owner || run.Id.Length > 160) continue;
                result.Add(new("runtime", run.Id, run.Scope.AccessScopeRef, run.ExecutionScopeRef, owner, agent.Namespace, uid,
                    agent.RevisionUid, agent.Version.ToString(CultureInfo.InvariantCulture), agent.DefinitionHash, State(run.Status.State), run.Status.CreatedAt,
                    run.Status.StartedAt, run.Status.CompletedAt, run.Status.InputTokens, run.Status.OutputTokens,
                    run.Status.InputTokens is not null && run.Status.OutputTokens is not null ? "complete" : "unavailable",
                    await PlacementAsync(context, RuntimeAssignmentTargetKind.RuntimeRun, run.Id, presence, ct)));
            }
        }
        return new(result, more, "attested-window");
    }
    private async Task<IReadOnlyList<SystemPlacementEvidence>> PlacementAsync(SystemOperationsReadContext context, RuntimeAssignmentTargetKind kind, string runId, IReadOnlyDictionary<Guid, string> presence, CancellationToken ct)
    {
        var value = await placement.GetPlacementExactAsync(context.DataScope, kind, runId, ct);
        if (value is null || value.ExecutionScopeRef != context.DataScope) return [];
        return [new(value.AssignmentId, value.State, value.Attempts.OrderByDescending(attempt => attempt.AcquiredAt).Take(8).Select(attempt =>
            new SystemWorkerEvidence(attempt.WorkerId, attempt.WorkerSessionId, attempt.AttemptId, attempt.FencingGeneration, attempt.State,
                attempt.AcquiredAt, attempt.LastHeartbeatAt, attempt.LeaseExpiresAt, attempt.CompletedAt, presence.GetValueOrDefault(attempt.WorkerId, "unknown"))).ToArray())];
    }
    private async Task<string?> WorkLinkAsync(FlowRun run, SystemOperationsReadContext context, CancellationToken ct)
    {
        if (!context.CanReadWork || !Guid.TryParse(run.WorkItemResourceId, out var id)) return null;
        using var active = flowScopes.Enter(SystemOperationsAccess.FlowScope(context));
        var value = await work.GetExactAsync(context.DataScope, new WorkItemId(id), ct);
        return value?.Value.ExecutionScopeRef == context.DataScope && value.Value.OwnerPrincipalId == context.ActorId
            && Guid.TryParse(run.RootFlowRunId ?? run.Id, out var runId) && value.Value.CurrentExecutionId?.Value == runId ? id.ToString("D") : null;
    }
    public async Task<long?> CurrentGenerationAsync(SystemGraphIdentity identity, CancellationToken ct)
    {
        var context = await graphAuthorization.GetContextAsync(ct);
        using var trusted = metadataScope.Enter();
        if (!(await graphAuthorization.EvaluateAsync(identity, context, ct)).InspectBindings) return null;
        var value = await identities.GetIdentityByUidAsync(identity.ResourceUid, ct);
        return value is not null && value.ScopeRef == identity.OwnerScope && value.Namespace == identity.Namespace && value.Kind == identity.Kind && value.Name == identity.Name
            ? value.Generation : null;
    }
    public async Task<string> ProvisioningAsync(SystemGraphNode root, CancellationToken ct)
    {
        using var trusted = metadataScope.Enter();
        var address = new ScopedResourceAddress(root.Identity.OwnerScope, root.Identity.Namespace, root.Identity.Kind, root.Identity.Name);
        if (root.Identity.Kind == "Agent") return (await resources.GetExactAsync<AgentResource>(address, ct))?.Value.Status?.ProvisioningState.ToString().ToLowerInvariant() ?? "unknown";
        if (root.Identity.Kind == "AgentRevision") return (await resources.GetExactAsync<AgentRevision>(address, ct))?.Value.ProvisioningState.ToString().ToLowerInvariant() ?? "unknown";
        return "unknown";
    }
    private static string State(RuntimeRunState state) => state switch
    { RuntimeRunState.Succeeded => "succeeded", RuntimeRunState.Failed => "failed", RuntimeRunState.Cancelled => "cancelled", RuntimeRunState.TimedOut => "timed-out", RuntimeRunState.Running => "running", _ => "pending" };
    private static string State(FlowRunStatus state) => state switch
    { FlowRunStatus.Succeeded => "succeeded", FlowRunStatus.Failed => "failed", FlowRunStatus.Cancelled => "cancelled", FlowRunStatus.TimedOut => "timed-out", FlowRunStatus.Running => "running", FlowRunStatus.WaitingForInput or FlowRunStatus.WaitingForChild => "waiting", _ => "pending" };
}
