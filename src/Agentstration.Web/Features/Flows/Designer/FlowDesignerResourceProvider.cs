using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerResourceProvider(IManagementApiClient client, IFlowApiClient flowClient, IToolsClient toolsClient) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await client.GetAgentsAsync(cancellationToken);
        return agents.Select(agent => new FlowDesignerAgent(agent.Name, agent.Name)).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(CancellationToken cancellationToken)
    {
        var flows = await flowClient.GetFlowsAsync(cancellationToken);
        return flows.Select(flow => new FlowDesignerFlow(flow.Id, flow.Name, flow.Namespace, flow.ActiveVersion)).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(
        Agentstration.Resources.ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var versions = await flowClient.GetFlowVersionsAsync(@namespace, name, cancellationToken);
        return versions.Select(version => new FlowDesignerFlowVersion(version.Version, version.Graph?.InputSchema, version.Graph?.OutputSchema)).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await toolsClient.GetToolsAsync(cancellationToken: cancellationToken);
        return tools.Where(tool => tool.Definition.Schema is not null).Select(tool => new FlowDesignerTool(
            tool.Metadata.Name,
            tool.Definition.DisplayName,
            tool.Metadata.Namespace,
            tool.Definition.Schema!.Input.Clone(),
            tool.Definition.Schema.Output?.Clone(),
            tool.Definition.Enabled,
            tool.Definition.Discovery?.Available == true,
            tool.Definition.RequiresApproval)).ToArray();
    }
}
