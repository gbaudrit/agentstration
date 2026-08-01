using Agentstration.Management.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Abstractions;

public sealed record AgentExecutionRequest(string Input, string? SessionId = null);
public sealed record AgentExecutionResult(string Output, string? SessionId = null);
public sealed record AgentRuntimeContext(IChatClientResolver ChatClients, IToolCatalog Tools);
public sealed record ProvisioningResult(bool Succeeded, string? Endpoint, string? Error);
public sealed record RuntimeObservation(OperationalState State, string? RevisionId, string? Error);
public sealed record ReconciliationResult(AgentDeployment Deployment, bool Changed, string Reason);
public sealed record AgentRouteRequest(string Input);
public sealed record RoutableAgent(string AgentId, string Description, IReadOnlyCollection<string> Capabilities);
public sealed record AgentRouteResult(string AgentId, double Confidence, string Reason);

public interface IAgentRuntime
{
    string AgentId { get; }
    string RevisionId { get; }
    Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken);
}

public interface IAgentRuntimeFactory
{
    string Handler { get; }
    Task<IAgentRuntime> CreateAsync(ResolvedAgentDefinition definition, string revisionId, AgentRuntimeContext context, CancellationToken cancellationToken);
}

public interface IChatClientResolver { IChatClient Resolve(string modelProfileId); }
public interface IToolCatalog { IReadOnlyCollection<AITool> Resolve(IEnumerable<string> toolIds); }

public interface IAgentDeploymentProvisioner
{
    AgentHostingMode HostingMode { get; }
    Task<ProvisioningResult> ProvisionAsync(AgentRevision revision, AgentDeployment deployment, CancellationToken cancellationToken);
    Task<ProvisioningResult> DeprovisionAsync(AgentDeployment deployment, CancellationToken cancellationToken);
    Task<RuntimeObservation> ObserveAsync(AgentDeployment deployment, CancellationToken cancellationToken);
}

public interface IAgentDeploymentReconciler
{
    Task<ReconciliationResult> ReconcileAsync(AgentDeployment deployment, CancellationToken cancellationToken);
}

public interface IRuntimeRegistry
{
    void Set(string deploymentId, IAgentRuntime runtime);
    bool TryGet(string deploymentId, out IAgentRuntime? runtime);
    bool Remove(string deploymentId);
    Task<AgentExecutionResult> ExecuteAsync(string deploymentId, AgentExecutionRequest request, CancellationToken cancellationToken);
}

public interface IAgentRouter
{
    Task<AgentRouteResult> SelectAsync(AgentRouteRequest request, IReadOnlyCollection<RoutableAgent> candidates, CancellationToken cancellationToken);
}
