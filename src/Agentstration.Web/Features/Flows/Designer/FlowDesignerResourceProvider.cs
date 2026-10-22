using Agentstration.Flows;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerResourceProvider(IManagementApiClient client, IFlowApiClient flowClient, IToolsClient toolsClient, ISchemasClient schemasClient) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await client.GetAgentsAsync(cancellationToken);
        return agents.Select(agent => new FlowDesignerAgent(agent.Id, agent.Name) { Namespace = agent.Namespace }).ToArray();
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
        return versions.Select(version => new FlowDesignerFlowVersion(version.Version, version.Graph?.InputSchema, version.Graph?.OutputSchema,
            version.Graph?.GetOutputs().Select(output => new FlowDesignerOutput(output.Name, output.DisplayName, output.Outcome, output.Schema)).ToArray() ?? [])).ToArray();
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

    public async Task<IReadOnlyList<FlowDesignerSchemaVersion>> GetSchemaVersionsAsync(CancellationToken cancellationToken)
    {
        var schemas = (await schemasClient.GetSchemasAsync(cancellationToken))
            .Where(schema => string.Equals(schema.Definition.Format, SchemaFormatIds.JsonSchema, StringComparison.Ordinal))
            .ToArray();
        var result = new List<FlowDesignerSchemaVersion>();
        foreach (var schema in schemas)
        {
            if (schema.ScopeRef is not { } scopeRef) continue;
            var versions = await schemasClient.GetVersionsAsync(scopeRef, schema.Namespace, schema.Name, cancellationToken);
            result.AddRange(versions.Select(version => new FlowDesignerSchemaVersion(
                schema.Name, schema.Definition.DisplayName, schema.Namespace, scopeRef, version.Version,
                version.Format, version.Dialect, version.Content.Clone())));
        }
        return result;
    }

    public Task<SchemaFormatValidationResult> ValidateSchemaDefinitionAsync(ValidateSchemaDefinitionRequest request,
        CancellationToken cancellationToken) => schemasClient.ValidateDefinitionAsync(request, cancellationToken);

    public Task<SchemaFormatValidationResult> ValidateSchemaInstanceAsync(ValidateSchemaInstanceRequest request,
        CancellationToken cancellationToken) => schemasClient.ValidateInstanceAsync(request, cancellationToken);
}
