using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Flows;

public sealed class AgentRevisionRunRetention(FlowRevisionRetentionService flowRuns) : IAgentRevisionRunRetention
{
    public async Task<AgentRevisionRunUsage> GetUsageAsync(string revisionName, CancellationToken cancellationToken) =>
        Map(await flowRuns.GetUsageAsync(revisionName, cancellationToken));

    public async Task<AgentRevisionRunUsage> ForceTerminateAsync(string revisionName, CancellationToken cancellationToken) =>
        Map(await flowRuns.ForceTerminateAsync(revisionName, cancellationToken));

    private static AgentRevisionRunUsage Map(FlowRevisionUsage usage) => new(
        usage.RevisionId,
        usage.ActiveRunCount,
        usage.WaitingForInputCount,
        usage.HistoricalRunCount,
        usage.ActiveRunIds);
}
