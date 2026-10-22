using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Flows.Application;

internal sealed record ResolvedFlowSchemaBindings(
    FlowGraphDefinition Graph,
    IReadOnlyDictionary<string, SchemaBindingSnapshot> Snapshots);

internal sealed class FlowSchemaBindingSnapshotter(ISchemaBindingResolver resolver)
{
    private const string JsonSchemaFormat = "json-schema";
    private const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    public async ValueTask<ResolvedFlowSchemaBindings> ResolveAsync(
        WorkspaceId workspaceId,
        FlowId flowId,
        FlowGraphDefinition graph,
        CancellationToken cancellationToken)
    {
        var context = new SchemaBindingResolutionContext(ResourceScopeRef.Workspace(workspaceId.Value),
            flowId.Namespace, JsonSchemaFormat);
        var snapshots = new Dictionary<string, SchemaBindingSnapshot>(StringComparer.Ordinal);
        var input = await ResolveAsync("$.inputSchema", graph.InputSchema, graph.InputSchemaBinding,
            context, snapshots, cancellationToken);
        var output = await ResolveAsync("$.outputSchema", graph.OutputSchema, graph.OutputSchemaBinding,
            context, snapshots, cancellationToken);
        var steps = new List<FlowStepDefinition>(graph.Steps.Count);
        foreach (var step in graph.Steps)
        {
            switch (step)
            {
                case InputFlowStepDefinition inputStep:
                    var inputStepSchema = await ResolveAsync($"$.steps[{inputStep.Name}].schema",
                        inputStep.Schema, inputStep.SchemaBinding, context, snapshots, cancellationToken);
                    steps.Add(inputStep with { Schema = inputStepSchema, SchemaBinding = null });
                    break;
                case TransformFlowStepDefinition transformStep:
                    var transformStepSchema = await ResolveAsync($"$.steps[{transformStep.Name}].schema",
                        transformStep.Schema, transformStep.SchemaBinding, context, snapshots, cancellationToken);
                    steps.Add(transformStep with { Schema = transformStepSchema, SchemaBinding = null });
                    break;
                case OutputFlowStepDefinition outputStep:
                    var outputStepSchema = await ResolveAsync($"$.steps[{outputStep.Name}].schema",
                        outputStep.Schema, outputStep.SchemaBinding, context, snapshots, cancellationToken);
                    steps.Add(outputStep with { Schema = outputStepSchema, SchemaBinding = null });
                    break;
                default:
                    steps.Add(step);
                    break;
            }
        }
        return new(graph with
        {
            InputSchema = input,
            InputSchemaBinding = null,
            OutputSchema = output,
            OutputSchemaBinding = null,
            Steps = steps
        }, snapshots);
    }

    private async ValueTask<JsonElement?> ResolveAsync(
        string path,
        JsonElement? legacy,
        SchemaBinding? binding,
        SchemaBindingResolutionContext context,
        Dictionary<string, SchemaBindingSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (legacy is not null && binding is not null)
            throw new FlowValidationException("schema_binding_ambiguous",
                $"Flow contract '{path}' cannot declare both a legacy JSON schema and a Schema binding.");
        if (legacy is null && binding is null) return null;
        var selected = binding ?? new SchemaBinding
        {
            Inline = new InlineSchemaBinding
            {
                Format = JsonSchemaFormat,
                Dialect = JsonSchemaDialect,
                Content = legacy!.Value.Clone()
            }
        };
        try
        {
            var snapshot = await resolver.ResolveAsync(path, selected, context, cancellationToken);
            snapshots.Add(path, snapshot);
            return snapshot.Content.Clone();
        }
        catch (SchemaBindingResolutionException exception)
        {
            throw new FlowValidationException(exception.Code, exception.Message);
        }
    }
}
