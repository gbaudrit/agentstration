using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Console.Web.Features.Flows;

public sealed class FlowDesignerResourceProvider(
    IManagementApiClient client,
    IFlowApiClient flowClient,
    IToolsClient toolsClient,
    IToolDefinitionsClient toolDefinitionsClient,
    ISchemasClient schemasClient) : IFlowDesignerResourceProvider
{
    public async Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken) =>
        (await client.GetAgentsAsync(contextScope, cancellationToken)).Where(agent => agent.ScopeRef is not null)
            .Select(agent => new FlowDesignerAgent(agent.Id, agent.Name, agent.ScopeRef!.Value)
            {
                Namespace = agent.Namespace,
                InputSchema = agent.InputSchema?.Clone(),
                InputSchemaReference = agent.InputSchemaReference
            }).ToArray();

    public async Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken) =>
        (await flowClient.GetFlowsAsync(contextScope, cancellationToken)).Where(flow => flow.ScopeRef is not null)
            .Select(flow => new FlowDesignerFlow(flow.Id, flow.Name, flow.Namespace, flow.ScopeRef!.Value, flow.ActiveVersion)).ToArray();

    public async Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(
        ResourceScopeRef scopeRef,
        Agentstration.Resources.ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        (await flowClient.GetFlowVersionsAsync(@namespace, name, scopeRef, cancellationToken))
            .Select(version =>
            {
                var input = version.Graph?.Steps.OfType<InputFlowStepDefinition>().SingleOrDefault();
                var inputReference = input?.Schema is not null
                    ? version.SchemaBindings?.GetValueOrDefault($"$.steps[{input.Name}].schema")?.Source
                    : version.SchemaBindings?.GetValueOrDefault("$.inputSchema")?.Source;
                return new FlowDesignerFlowVersion(version.Version,
                input?.Schema ?? version.Graph?.InputSchema,
                version.Graph?.OutputSchema,
                version.Graph?.GetOutputs().Select(output => new FlowDesignerOutput(output.Name, output.DisplayName, output.Outcome, output.Schema)).ToArray() ?? [],
                inputReference);
            }).ToArray();

    public async Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
    {
        var toolsTask = toolsClient.GetToolsAsync(contextScope, cancellationToken: cancellationToken);
        var definitionsTask = toolDefinitionsClient.GetAsync(cancellationToken);
        await Task.WhenAll(toolsTask, definitionsTask);
        var definitions = (await definitionsTask).ToDictionary(
            definition => (definition.Namespace, definition.Name));
        return (await toolsTask)
            .Where(tool => tool.Definition.Schema is not null && tool.ScopeRef is not null)
            .Select(tool => new FlowDesignerTool(
                tool.Metadata.Name,
                tool.Definition.DisplayName,
                tool.Metadata.Namespace,
                tool.Definition.Schema!.Input.Clone(),
                tool.Definition.Schema.Output?.Clone(),
                tool.Definition.Enabled,
                tool.Definition.Discovery?.Available == true,
                tool.Definition.RequiresApproval,
                tool.ScopeRef!.Value)
            {
                InputSchemaReference = ToolInputSchemaReference(tool, definitions)
            })
            .ToArray();
    }

    private static SchemaVersionReference? ToolInputSchemaReference(
        Agentstration.Tools.ToolResource tool,
        IReadOnlyDictionary<(Agentstration.Resources.ResourceNamespace Namespace, string Name), Agentstration.Tools.ToolDefinitionResource> definitions)
    {
        if (tool.Definition.Provider?.Name != Agentstration.Tools.AgentstrationToolProvider.Name
            || string.IsNullOrWhiteSpace(tool.Definition.ExternalId))
            return null;
        return definitions.GetValueOrDefault((tool.Namespace, tool.Definition.ExternalId))?.Definition.InputSchemaBinding?.Reference;
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
