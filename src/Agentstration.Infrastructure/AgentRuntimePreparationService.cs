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
        var deployment = await agents.PrepareLocalRuntimeAsync(resourceNamespace, agentName, generation, cancellationToken);
        return new(
            deployment.Value.Metadata.Name,
            deployment.Value.RevisionName,
            deployment.Value.OperationalState.ToString());
    }
}
