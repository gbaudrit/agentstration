using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerResourceProvider(IManagementApiClient client) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await client.GetAgentsAsync(cancellationToken);
        return agents.Select(agent => new FlowDesignerAgent(agent.Name, agent.Name)).ToArray();
    }
}
