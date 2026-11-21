using Agentstration.Resources;

namespace Agentstration.SystemGraph.Contracts;

public sealed record SystemGraphImpactQuery(string Root, ResourceNamespace? Namespace = null, int MaximumDepth = 16, int MaximumPaths = 200);
public sealed record SystemGraphChangeQuery(string From, string To, ResourceNamespace? Namespace = null, int MaximumDepth = 16, int MaximumPaths = 200);
public sealed record SystemGraphImpactPath(IReadOnlyList<string> Nodes, IReadOnlyList<SystemGraphRelation> Relations, string Assessment);
public sealed record SystemGraphImpactResult(
    IReadOnlyList<SystemGraphNode> Nodes,
    IReadOnlyList<SystemGraphImpactPath> Paths,
    string Snapshot,
    DateTimeOffset ObservedAt,
    bool Truncated,
    bool HasCycles,
    bool GloballyComplete,
    IReadOnlyList<string> Limitations);

public sealed record SystemGraphChangeResult(string Assessment, SystemGraphImpactResult Previous, SystemGraphImpactResult Current);

public interface ISystemGraphImpactService
{
    Task<SystemGraphImpactResult> AnalyzeAsync(SystemGraphImpactQuery query, CancellationToken cancellationToken);
    Task<SystemGraphChangeResult> CompareAsync(SystemGraphChangeQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SystemGraphContributorCoverage>> GetCoverageAsync(CancellationToken cancellationToken);
}
