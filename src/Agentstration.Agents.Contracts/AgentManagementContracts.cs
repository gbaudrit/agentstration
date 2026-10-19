using Agentstration.Agents;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Agents.Contracts;

public sealed record AgentResourceRequest : ResourceDeclaration<AgentProperties>;
public sealed record CreateRevisionRequest(string Environment, string RuntimeProfileName, AgentHostingMode HostingMode, string RuntimeProfileNamespace = "default");
public sealed record CreateDeploymentRequest(string RevisionName, string Environment, string RuntimeProfileName, AgentHostingMode HostingMode, string RuntimeProfileNamespace = "default");
public sealed record AgentRevisionRunUsageResponse(
    int ActiveRunCount,
    int WaitingForInputCount,
    int HistoricalRunCount,
    IReadOnlyList<string> ActiveRunIds,
    IReadOnlyList<AgentRevisionRunImpactResponse> ActiveRuns);
public sealed record AgentRevisionRunImpactResponse(
    string RunId,
    string Status,
    int PendingInputRequestCount);
public sealed record AgentRevisionPurgeImpactResponse(
    string Namespace,
    string AgentName,
    string RevisionName,
    long AgentGeneration,
    bool ProtectedByRetentionPolicy,
    IReadOnlyList<string> ProtectionReasons,
    string? DeploymentName,
    AgentRevisionRunUsageResponse RunUsage);
public sealed record RouteAndExecuteRequest(string Input);
public sealed record RouteAndExecuteResponse(string AgentName, double Confidence, string Reason, string Output);
