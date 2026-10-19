using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Agents;

public sealed record AgentRevisionRunUsage(
    string RevisionName,
    int ActiveRunCount,
    int WaitingForInputCount,
    int HistoricalRunCount,
    IReadOnlyList<string> ActiveRunIds,
    IReadOnlyList<AgentRevisionRunImpact> ActiveRuns);

public sealed record AgentRevisionRunImpact(string RunId, string Status, int PendingInputRequestCount);

public interface IAgentRevisionRunRetention
{
    Task<AgentRevisionRunUsage> GetUsageAsync(string revisionName, CancellationToken cancellationToken);
    Task<AgentRevisionRunUsage> ForceTerminateAsync(string revisionName, CancellationToken cancellationToken);
}

public interface IAgentResourceQueries
{
    Task<StoredResource<AgentRevision>?> FindRevisionAsync(Guid agentUid, long generation, CancellationToken cancellationToken);
    Task<StoredResource<AgentRevision>?> FindLatestRevisionAsync(Guid agentUid, CancellationToken cancellationToken);
    Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(string revisionName, CancellationToken cancellationToken);
    Task<StoredResource<AgentDeployment>?> FindDeploymentByRevisionAsync(ResourceNamespace @namespace, string revisionName, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(string agentName, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsForAgentAsync(ResourceNamespace @namespace, string agentName, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(CancellationToken cancellationToken);
}

public interface IModelProfileReferenceValidator
{
    Task ValidateAsync(ResourceReference profileReference, ResourceNamespace ownerNamespace, ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken);
}
