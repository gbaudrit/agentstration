using System.Text.Json;
using Agentstration.Flows;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowDesigner
{
    private IReadOnlyList<string> ConnectionWarnings()
    {
        if (Editor.State.Resource is not { } resource) return [];
        var warnings = new List<string>();
        foreach (var edge in resource.Definition.Transitions)
        {
            var source = resource.Definition.Steps.FirstOrDefault(step => step.Name == edge.FromStep);
            var target = resource.Definition.Steps.FirstOrDefault(step => step.Name == edge.ToStep);
            if (source is null || target is null || PortSchema(source, edge.Event) is not { } output || PortSchema(target, "input") is not { } input) continue;
            // Process/result carry content and private access envelopes, not the manifest returned by completed.
            if (source is ArtifactTransformFlowStepDefinition || target is ArtifactTransformFlowStepDefinition) continue;
            if (target is ToolFlowStepDefinition { ArgumentsMapping: null }) continue;
            var mapping = target switch
            {
                AgentFlowStepDefinition agent => agent.InputMapping,
                ToolFlowStepDefinition tool => tool.ArgumentsMapping,
                FlowCallStepDefinition call => call.InputMapping,
                OutputFlowStepDefinition result => result.OutputMapping,
                _ => null
            };
            if (MappedSchema(mapping, output) is not { } mapped || !FlowSchemaCompatibility.IsSupported(mapped) || !FlowSchemaCompatibility.IsSupported(input)) continue;
            if (!FlowSchemaCompatibility.IsAssignable(mapped, input))
                warnings.Add(T("ConnectionContractMismatch", source.DisplayName ?? source.Name, target.DisplayName ?? target.Name));
        }
        return warnings;
    }

    // Only infer mappings whose shape is statically known. Runtime expressions remain unresolved.
    private static JsonElement? MappedSchema(JsonElement? mapping, JsonElement incoming, int depth = 0)
    {
        if (depth > 12) return null;
        if (mapping is null) return incoming;
        var value = mapping.Value;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            if (text == "${transition.output}") return incoming;
            if (text.StartsWith("${transition.output.", StringComparison.Ordinal) && text.EndsWith('}'))
            {
                var schema = incoming;
                foreach (var name in text[20..^1].Split('.'))
                    if (schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(value => value.GetString() == name)
                        && schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(name, out var child)) schema = child;
                    else return null;
                return schema;
            }
            if (text.Contains("${", StringComparison.Ordinal)) return null;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = new Dictionary<string, JsonElement>();
            foreach (var property in value.EnumerateObject())
            {
                if (MappedSchema(property.Value, incoming, depth + 1) is not { } schema) return null;
                properties[property.Name] = schema;
            }
            return JsonSerializer.SerializeToElement(new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false });
        }
        if (value.ValueKind == JsonValueKind.Array) return null;
        var type = value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" };
        return JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["type"] = type, ["enum"] = new[] { value } });
    }
}
