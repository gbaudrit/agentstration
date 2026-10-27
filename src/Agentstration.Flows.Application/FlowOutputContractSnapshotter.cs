using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

internal sealed class FlowOutputContractSnapshotter(IFlowResourceReferenceResolver? resources)
{
    public async ValueTask<FlowGraphDefinition> ResolveAsync(
        ResourceScopeRef ownerScopeRef,
        FlowId flowId,
        FlowGraphDefinition graph,
        CancellationToken cancellationToken)
    {
        var steps = new List<FlowStepDefinition>(graph.Steps.Count);
        foreach (var step in graph.Steps)
        {
            if (step is not OutputFlowStepDefinition output
                || output.Schema is not null
                || output.SchemaBinding is not null
                || !IsPassThrough(output.OutputMapping))
            {
                steps.Add(step);
                continue;
            }

            var candidates = new List<JsonElement>();
            foreach (var transition in graph.Transitions.Where(candidate => candidate.ToStep == output.Name))
            {
                var source = graph.Steps.FirstOrDefault(candidate => candidate.Name == transition.FromStep);
                var schema = await ResolveTransitionSchemaAsync(
                    ownerScopeRef, flowId.Namespace, graph, source, transition.Event, cancellationToken);
                if (schema is { } value
                    && !candidates.Any(candidate => JsonElement.DeepEquals(candidate, value)))
                    candidates.Add(value.Clone());
            }

            if (candidates.Count > 1)
                throw new FlowValidationException(
                    "flow_output_schema_ambiguous",
                    $"Schema inference for named output '{output.Name}' is ambiguous. Declare its schema explicitly.");

            steps.Add(candidates.Count == 1 ? output with { Schema = candidates[0] } : output);
        }

        return graph with { Steps = steps };
    }

    private async ValueTask<JsonElement?> ResolveTransitionSchemaAsync(
        ResourceScopeRef ownerScopeRef,
        ResourceNamespace ownerNamespace,
        FlowGraphDefinition graph,
        FlowStepDefinition? source,
        string outputEvent,
        CancellationToken cancellationToken)
    {
        switch (source)
        {
            case InputFlowStepDefinition input:
                return input.Schema ?? graph.InputSchema;
            case TransformFlowStepDefinition transform:
                return transform.Schema;
            case ToolFlowStepDefinition tool when resources is not null
                                                  && string.Equals(outputEvent, "success", StringComparison.Ordinal):
                return (await resources.ResolveToolAsync(
                    ownerScopeRef, ownerNamespace, tool.Tool, cancellationToken))?.OutputSchema;
            case ToolRouteFlowStepDefinition toolRoute when resources is not null
                                                           && string.Equals(outputEvent, "success", StringComparison.Ordinal):
                return (await resources.ResolveToolRouteAsync(
                    ownerScopeRef, ownerNamespace, toolRoute, cancellationToken))?.OutputSchema;
            case FlowCallStepDefinition call when resources is not null:
                var resolved = await resources.ResolveFlowAsync(
                    ownerScopeRef, ownerNamespace, call.Flow, cancellationToken);
                return resolved?.Outputs?.FirstOrDefault(output => output.Name == outputEvent)?.Schema
                    ?? resolved?.OutputSchema;
            default:
                return null;
        }
    }

    private static bool IsPassThrough(JsonElement? mapping) =>
        mapping is null
        || mapping is { ValueKind: JsonValueKind.String } value
            && string.Equals(value.GetString(), "${transition.output}", StringComparison.Ordinal);
}
