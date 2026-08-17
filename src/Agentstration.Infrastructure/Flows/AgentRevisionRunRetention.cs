using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Flows;

public sealed class AgentRevisionRunRetention(
    FlowRevisionRetentionService flowRuns,
    IRuntimeExecutionStateStore executionStates) : IAgentRevisionRunRetention
{
    public async Task<AgentRevisionRunUsage> GetUsageAsync(string revisionName, CancellationToken cancellationToken) =>
        Map(await flowRuns.GetUsageAsync(revisionName, cancellationToken));

    public async Task<AgentRevisionRunUsage> ForceTerminateAsync(string revisionName, CancellationToken cancellationToken)
    {
        var usage = await flowRuns.ForceTerminateAsync(revisionName, cancellationToken);
        foreach (var runId in usage.ActiveRunIds)
            await executionStates.DeleteAsync(runId, null, cancellationToken);
        return Map(usage);
    }

    private static AgentRevisionRunUsage Map(FlowRevisionUsage usage) => new(
        usage.RevisionId,
        usage.ActiveRunCount,
        usage.WaitingForInputCount,
        usage.HistoricalRunCount,
        usage.ActiveRunIds,
        usage.ActiveRuns.Select(run => new AgentRevisionRunImpact(
            run.RunId,
            run.Status.ToString(),
            run.PendingInputRequestCount)).ToArray());
}
