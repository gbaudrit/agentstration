using Agentstration.Agents;

namespace Agentstration.Agents;

public sealed record AgentDeploymentReconciliationResult(AgentDeployment Deployment, bool Changed, string Reason);

public interface IAgentDeploymentReconciler
{
    Task<AgentDeploymentReconciliationResult> ReconcileAsync(AgentDeployment deployment, CancellationToken cancellationToken);
}
