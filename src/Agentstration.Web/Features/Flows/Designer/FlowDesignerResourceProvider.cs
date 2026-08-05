using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerResourceProvider(IManagementApiClient client) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(string resourceGroup, CancellationToken cancellationToken)
    {
        var group = string.IsNullOrWhiteSpace(resourceGroup) ? "default" : resourceGroup.Trim();
        var agents = await client.GetAgentsAsync(group, cancellationToken);
        return agents.Select(agent => new FlowDesignerAgent(agent.Id, agent.Name, group)).ToArray();
    }
}
