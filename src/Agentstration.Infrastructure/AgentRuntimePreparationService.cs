using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure;

public sealed class AgentRuntimePreparationService(AgentManagementService agents) : IAgentRuntimePreparationService
{
    public async Task<PreparedAgentRuntime> PrepareAsync(
        ResourceNamespace resourceNamespace,
        string agentName,
        long generation,
        CancellationToken cancellationToken)
    {
        var revision = await agents.EnsureExecutionRevisionAsync(resourceNamespace, agentName, generation, cancellationToken);
        return new(
            string.Empty,
            revision.Value.Metadata.Name,
            "Ready");
    }
}

public sealed class ExternalWorkerAgentDeploymentReconciler : IAgentDeploymentReconciler
{
    public Task<AgentDeploymentReconciliationResult> ReconcileAsync(
        AgentDeployment deployment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deployment.DesiredState == DesiredAgentState.Stopped)
        {
            var stopped = deployment with
            {
                ProvisioningState = ProvisioningState.Succeeded,
                OperationalState = OperationalState.Stopped,
                ObservedRevisionName = null,
                LastError = null
            };
            return Task.FromResult(new AgentDeploymentReconciliationResult(stopped, stopped != deployment, "Deployment is stopped."));
        }

        var unsupported = deployment with
        {
            ProvisioningState = ProvisioningState.Failed,
            OperationalState = OperationalState.Unavailable,
            ObservedRevisionName = null,
            LastError = "Agent deployments are not materialized by the authoritative server. Runtime execution is assigned to external AWP Workers."
        };
        return Task.FromResult(new AgentDeploymentReconciliationResult(unsupported, unsupported != deployment, unsupported.LastError));
    }
}
