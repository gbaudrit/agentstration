using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;

namespace Agentstration.SystemOperations;

public sealed class SystemOperationsException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Separate volatile read model. Never mutates topology, publication or execution state.</summary>
public sealed class SystemOperationsService(ISystemGraphQueryService graph, ISystemOperationsAccess access,
    ISystemOperationsEvidenceReader evidence, TimeProvider clock, SystemOperationsTelemetry telemetry) : ISystemOperationsService
{
    public async Task<SystemOperationalSnapshot> SnapshotAsync(SystemOperationsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Root) || query.Root.Length > 512 || query.WindowMinutes is < 1 or > 1440 || query.MaximumRuns is < 1 or > 100)
            throw new SystemOperationsException("system_operations_query_invalid");
        var context = await access.GetAsync(ct);
        var page = await graph.QueryAsync(new(query.Namespace, query.Root, 200), ct);
        var root = page.Nodes.FirstOrDefault(node => node.Id == query.Root);
        var now = clock.GetUtcNow();
        if (root is null) return new(null, now, "unavailable");
        using var activity = telemetry.Start(root.Id);
        var generationNodes = page.Nodes.Where(node => node.State == "recorded-generation" && node.Evidence?.Generation is not null
            && page.Relations.Any(edge => edge.Kind == "exact-dependency" && edge.Source == root.Id && edge.Target == node.Id
                || edge.Kind == "generation-of" && edge.Source == node.Id && edge.Target == root.Id)).Take(32).ToArray();
        if (root.State == "recorded-generation") generationNodes = [root];
        var dependency = "unknown";
        if (generationNodes.Length > 0)
        {
            var known = true; var stale = false;
            foreach (var pin in generationNodes)
            {
                var current = await evidence.CurrentGenerationAsync(pin.Identity, ct);
                known &= current is not null;
                stale |= current is not null && current != pin.Evidence!.Generation;
            }
            dependency = stale ? root.State == "definition" ? "observed-consumers-stale" : "observed-pins-stale"
                : known ? "observed-pins-current" : "unknown";
        }
        var start = now.AddMinutes(-query.WindowMinutes);
        var rows = context.CanReadRuns && root.Identity.Kind is "Agent" or "AgentRevision" or "Flow"
            ? await evidence.ReadAsync(root, context, start, query.MaximumRuns, ct)
            : new SystemRunEvidencePage([], false, context.CanReadRuns ? "family-unsupported" : "run-read-required");
        var runs = rows.Runs.Where(run => Matches(root, run, context)).DistinctBy(run => (run.Family, run.DataScope, run.RunId))
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.RunId, StringComparer.Ordinal).Take(query.MaximumRuns).ToArray();
        var bindings = rows.FlowBindings.Where(binding => MatchesBinding(root, binding, context))
            .DistinctBy(binding => (binding.FlowRunId, binding.DataScope, binding.ParticipantId, binding.AgentUid, binding.RevisionUid))
            .OrderByDescending(binding => binding.RunCreatedAt).ThenBy(binding => binding.FlowRunId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ParticipantId, StringComparer.Ordinal).Take(query.MaximumRuns).ToArray();
        var metrics = runs.Length == 0 ? null : Metrics(runs);
        var presences = runs.SelectMany(run => run.Placements).SelectMany(placement => placement.Attempts).Select(attempt => attempt.Presence).Concat(bindings.SelectMany(binding => binding.Invocations).Select(invocation => invocation.Presence)).ToArray();
        var presence = !context.CanInspectWorkers || presences.Length == 0 ? "unknown" : presences.Contains("offline", StringComparer.Ordinal) ? "offline"
            : presences.Contains("stale", StringComparer.Ordinal) ? "stale" : presences.All(value => value == "online") ? "online" : "unknown";
        var provisioning = await evidence.ProvisioningAsync(root, ct);
        if (!await access.CanDeliverAsync(root.Identity, context, generationNodes.Length > 0, ct))
            throw new SystemOperationsException("system_operations_access_changed");
        var currentPage = await graph.QueryAsync(new(query.Namespace, query.Root, 200), ct);
        if (currentPage.Snapshot != page.Snapshot) throw new SystemOperationsException("system_operations_access_changed");
        var sampledAt = clock.GetUtcNow();
        var facts = new SystemOperationalFacts(root.Id, root.Identity, root.State, provisioning, dependency, presence, metrics,
            runs, context.InvocationScope, context.DataScope, start, sampledAt, rows.HasMore ? "bounded-sample" : rows.Coverage,
            context.CanInspectWorkers, ["attested-identities-only", "legacy-identity-evidence-unavailable", "active-now-and-terminal-update-window", "duration-is-per-completed-run", "no-global-worker-totals", "flow-bindings-are-not-direct-execution-metrics"])
        { FlowBindings = bindings, MoreFlowBindings = rows.MoreFlowBindings };
        telemetry.Record(root.Identity.Kind, facts.Coverage);
        return new(facts, sampledAt, facts.Coverage);
    }
    private static bool Matches(SystemGraphNode root, SystemRunEvidence run, SystemOperationsReadContext context)
    {
        if (run.InvocationScope != context.InvocationScope || run.DataScope != context.DataScope || run.DefinitionOwner != root.Identity.OwnerScope || run.DefinitionNamespace != root.Identity.Namespace) return false;
        if (root.Identity.Kind == "AgentRevision") return run.RevisionUid == root.Identity.ResourceUid
            && (root.Evidence?.Hash is null || root.Evidence.Hash == run.DefinitionHash);
        if (run.ResourceUid != root.Identity.ResourceUid) return false;
        if (root.State == "recorded-generation" && run.Publication != root.Evidence?.Generation?.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        return root.State != "publication" || root.Evidence?.Publication == run.Publication
            && (root.Evidence?.Hash is null || root.Evidence.Hash == run.DefinitionHash);
    }
    private static bool MatchesBinding(SystemGraphNode root, SystemFlowBindingEvidence binding, SystemOperationsReadContext context)
    {
        if (root.Identity.Kind is not ("Agent" or "AgentRevision") || binding.AgentUid == Guid.Empty || binding.RevisionUid == Guid.Empty
            || binding.Generation <= 0 || binding.ParticipantId.Length is < 1 or > 160 || binding.InvocationScope != context.InvocationScope
            || binding.DataScope != context.DataScope || binding.AgentOwner != root.Identity.OwnerScope || binding.AgentNamespace != root.Identity.Namespace) return false;
        return root.Identity.Kind == "AgentRevision" ? binding.RevisionUid == root.Identity.ResourceUid
            && (root.Evidence?.Hash is null || binding.DefinitionHash == root.Evidence.Hash)
            : binding.AgentUid == root.Identity.ResourceUid && (root.State != "recorded-generation" || binding.Generation == root.Evidence?.Generation);
    }
    private static SystemRunMetrics Metrics(SystemRunEvidence[] runs)
    {
        var completed = runs.Where(run => run.StartedAt is not null && run.CompletedAt >= run.StartedAt)
            .Select(run => (run.CompletedAt!.Value - run.StartedAt!.Value).TotalMilliseconds).ToArray();
        var inputs = runs.Where(run => run.InputTokens >= 0).Select(run => run.InputTokens!.Value).ToArray();
        var outputs = runs.Where(run => run.OutputTokens >= 0).Select(run => run.OutputTokens!.Value).ToArray();
        return new(runs.Count(run => run.State is "pending" or "running" or "waiting"), runs.Count(run => run.State == "succeeded"),
            runs.Count(run => run.State is "failed" or "timed-out"), runs.Count(run => run.State == "cancelled"),
            completed.Length == 0 ? null : completed.Average(), inputs.Length == 0 ? null : inputs.Sum(), outputs.Length == 0 ? null : outputs.Sum(),
            inputs.Length == runs.Length && outputs.Length == runs.Length && runs.All(run => run.TokenCoverage == "complete") ? "reported-complete" : inputs.Length + outputs.Length > 0 ? "reported-partial" : "unavailable");
    }
}
