using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Schemas;

namespace Agentstration.Web.Console;

/// <summary>Bounded, authorized reads for execution presentation. No execution is started or retried.</summary>
public static class FlowRunInspection
{
    private const int MaximumRelatedRuns = 16;

    public static string FlowLabel(FlowRun run) =>
        $"{(run.FlowId.Namespace.IsDefault ? run.FlowId.Value : run.FlowId.ToString())} · {run.FlowVersion}";

    public static string StepLabel(FlowRun run, string name) =>
        run.DefinitionSnapshot.Graph?.Steps.FirstOrDefault(step => step.Name == name)?.DisplayName
        ?? run.Steps.FirstOrDefault(step => step.StepName == name)?.AgentResourceId ?? name;

    public static string RunUrl(string id, ResourceScopeRef scope, ResourceScopeRef? logicalScope = null, string? step = null)
    {
        var url = $"/flow-runs/{Uri.EscapeDataString(id)}?scopeRef={Uri.EscapeDataString(scope.Value)}";
        if (logicalScope is { } context) url += $"&context={Uri.EscapeDataString(context.Value)}";
        if (step is not null) url += $"&step={Uri.EscapeDataString(step)}";
        return url;
    }

    public static (string RunId, string StepName)? ContractOwner(string origin)
    {
        if (!origin.StartsWith("flow:", StringComparison.Ordinal)) return null;
        var separator = origin.IndexOf("/step:", StringComparison.Ordinal);
        if (separator <= 5) return null;
        var tail = origin[(separator + 6)..];
        var end = tail.IndexOfAny(['/', ' ']);
        var step = end < 0 ? tail : tail[..end];
        return step.Length == 0 ? null : (origin[5..separator], step);
    }

    public static async Task<IReadOnlyList<FlowRun>> ReadActiveAsync(IFlowApiClient client, FlowRun root, CancellationToken token)
    {
        var result = new List<FlowRun> { root };
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        for (var index = 0; index < result.Count && result.Count < MaximumRelatedRuns; index++)
        {
            var parent = result[index];
            if (parent.Status.IsTerminal()) continue;
            foreach (var step in parent.Steps.Where(step => step.Status == FlowStepRunStatus.Running))
            {
                var ids = (step.ChildFlowRunId is { } primary ? new[] { primary } : [])
                    .Concat(step.ChildFlowRunIds.Reverse()).Distinct(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    if (visited.Count >= MaximumRelatedRuns) return result;
                    if (!visited.Add(id)) continue;
                    var child = await ReadAsync(client, id, root.ExecutionScopeRef, token);
                    if (child is not null && child.ParentFlowRunId == parent.Id && !child.Status.IsTerminal()) { result.Add(child); break; }
                }
            }
        }
        return result;
    }

    public static IReadOnlyList<(FlowRun Run, FlowStepRun? Step)> ActiveLocations(IReadOnlyList<FlowRun> runs)
    {
        var result = new List<(FlowRun, FlowStepRun?)>();
        foreach (var run in runs.Where(run => !run.Status.IsTerminal()))
        {
            var active = run.Steps.Where(step => step.Status == FlowStepRunStatus.Running).ToArray();
            foreach (var step in active)
            {
                if (runs.Any(child => child.ParentFlowRunId == run.Id
                    && (child.Id == step.ChildFlowRunId || step.ChildFlowRunIds.Contains(child.Id)))) continue;
                result.Add((run, step));
            }
            if (active.Length == 0) result.Add((run, null));
        }
        return result;
    }

    public static async Task<IReadOnlyList<FlowRun>> ReadErrorOriginsAsync(IFlowApiClient client, FlowRun root, CancellationToken token)
    {
        var ids = root.Steps.Select(step => step.Error).Prepend(root.Error)
            .Select(error => DataContracts.ParseFailure(error?.Details)).OfType<DataContractFailure>()
            .SelectMany(failure => new[] { failure.FlowRunId, ContractOwner(failure.Origin)?.RunId })
            .OfType<string>().Where(id => id != root.Id).Distinct(StringComparer.Ordinal).Take(MaximumRelatedRuns - 1);
        var result = new List<FlowRun> { root };
        foreach (var id in ids)
            if (await ReadAsync(client, id, root.ExecutionScopeRef, token) is { } related) result.Add(related);
        return result;
    }

    private static async Task<FlowRun?> ReadAsync(IFlowApiClient client, string id, ResourceScopeRef scope, CancellationToken token)
    {
        try
        {
            var run = await client.GetFlowRunAsync(id, scope, token);
            return run.Id == id && run.ExecutionScopeRef == scope ? run : null;
        }
        catch (AgentstrationApiException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
}
