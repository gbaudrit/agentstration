using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerResourceProvider(IManagementApiClient client, IFlowApiClient flowClient, IToolsClient toolsClient) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
    {
        var agents = await client.GetAgentsAsync(contextScope, cancellationToken);
        return agents.Where(agent => agent.ScopeRef is not null)
            .Select(agent => new FlowDesignerAgent(agent.Id, agent.Name, agent.ScopeRef!.Value) { Namespace = agent.Namespace }).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
    {
        var flows = await flowClient.GetFlowsAsync(contextScope, cancellationToken);
        return flows.Where(flow => flow.ScopeRef is not null)
            .Select(flow => new FlowDesignerFlow(flow.Id, flow.Name, flow.Namespace, flow.ScopeRef!.Value, flow.ActiveVersion)).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(
        ResourceScopeRef scopeRef,
        Agentstration.Resources.ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var versions = await flowClient.GetFlowVersionsAsync(@namespace, name, scopeRef, cancellationToken);
        return versions.Select(version => new FlowDesignerFlowVersion(version.Version, version.Graph?.InputSchema, version.Graph?.OutputSchema,
            version.Graph?.GetOutputs().Select(output => new FlowDesignerOutput(output.Name, output.DisplayName, output.Outcome, output.Schema)).ToArray() ?? [])).ToArray();
    }

    public async Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
    {
        var tools = await toolsClient.GetToolsAsync(contextScope, cancellationToken: cancellationToken);
        return tools.Where(tool => tool.Definition.Schema is not null && tool.ScopeRef is not null).Select(tool => new FlowDesignerTool(
            tool.Metadata.Name,
            tool.Definition.DisplayName,
            tool.Metadata.Namespace,
            tool.Definition.Schema!.Input.Clone(),
            tool.Definition.Schema.Output?.Clone(),
            tool.Definition.Enabled,
            tool.Definition.Discovery?.Available == true,
            tool.Definition.RequiresApproval,
            tool.ScopeRef!.Value)).ToArray();
    }
}
