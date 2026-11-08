using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Tools;

/// <summary>Bounded, ordinal JSON Pointer bindings over declared object properties.</summary>
public static class ToolRelayArguments
{
    public const int MaximumBindings = 64;
    public const int MaximumPayloadLength = 65_536;

    public static JsonElement PublicSchema(JsonElement source, IReadOnlyList<ToolArgumentBinding> bindings)
    {
        ValidatePaths(bindings);
        if (source.GetRawText().Length > MaximumPayloadLength) throw Invalid("The source schema exceeds the relay size limit.");
        RejectUnsupportedConstraints(source, 0);
        var root = JsonNode.Parse(source.GetRawText()) as JsonObject
            ?? throw Invalid("The source input schema must be an object schema.");
        foreach (var binding in bindings)
        {
            var schema = PropertySchema(source, binding.Path);
            if (binding is LiteralToolArgumentBinding literal)
            {
                try { ToolInputSchemaValidator.Validate(schema, literal.Value); }
                catch (ToolInputValidationException) { throw Invalid("A fixed literal does not satisfy its source property schema."); }
            }
            if (binding is SecretToolArgumentBinding
                && (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "string"))
                throw Invalid("A Secret binding requires an explicitly string-typed source property.");
        }
        Prune(root, bindings.Select(value => Segments(value.Path)).ToArray());
        return JsonSerializer.SerializeToElement(root);
    }

    public static JsonElement PropertySchema(JsonElement source, string path)
    {
        var current = source;
        foreach (var segment in Segments(path))
        {
            if (current.ValueKind != JsonValueKind.Object
                || current.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "object")
                || !current.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(segment, out current))
                throw Invalid("A fixed path must address declared object properties in the source schema.");
        }
        return current.Clone();
    }

    public static void ValidateCaller(JsonElement caller, IReadOnlyList<ToolArgumentBinding> bindings)
    {
        if (caller.ValueKind != JsonValueKind.Object || caller.GetRawText().Length > MaximumPayloadLength)
            throw Invalid("Relay input must be a bounded JSON object.");
        RejectDuplicateProperties(caller, 0);
        foreach (var binding in bindings)
        {
            var current = caller;
            var segments = Segments(binding.Path);
            for (var index = 0; index < segments.Length; index++)
            {
                if (current.ValueKind != JsonValueKind.Object)
                    throw Invalid("A caller cannot replace an object containing a fixed input.");
                if (!current.TryGetProperty(segments[index], out current)) break;
                if (index == segments.Length - 1)
                    throw new ToolDefinitionValidationException("tool_definition_fixed_argument_override", "A fixed input cannot be supplied by the caller.");
            }
        }
    }

    public static JsonElement Reconstruct(JsonElement caller, IReadOnlyList<ToolArgumentBinding> bindings,
        IReadOnlyDictionary<string, JsonElement> values, JsonElement sourceSchema)
    {
        ValidatePaths(bindings);
        ValidateCaller(caller, bindings);
        try { ToolInputSchemaValidator.Validate(PublicSchema(sourceSchema, bindings), caller); }
        catch (ToolInputValidationException) { throw Invalid("Caller input does not satisfy the effective relay contract."); }
        var root = JsonNode.Parse(caller.GetRawText())!.AsObject();
        foreach (var binding in bindings)
        {
            var value = values.TryGetValue(binding.Path, out var resolved) ? resolved
                : throw Invalid("A fixed binding has no resolved value.");
            var current = root;
            var segments = Segments(binding.Path);
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (current[segments[index]] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[segments[index]] = child;
                }
                current = child;
            }
            current[segments[^1]] = JsonNode.Parse(value.GetRawText());
        }
        var result = JsonSerializer.SerializeToElement(root);
        if (result.GetRawText().Length > MaximumPayloadLength) throw Invalid("Reconstructed Tool input exceeds its payload limit.");
        try { ToolInputSchemaValidator.Validate(sourceSchema, result); }
        catch (ToolInputValidationException) { throw Invalid("Reconstructed Tool input does not satisfy its pinned source contract."); }
        return result;
    }

    public static string[] Segments(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > 1_024)
            throw Invalid("Use a non-root JSON Pointer for each fixed input.");
        var parts = path[1..].Split('/');
        if (parts.Length > 12) throw Invalid("Fixed input paths cannot exceed twelve object properties.");
        return parts.Select(part =>
        {
            for (var index = 0; index < part.Length; index++)
                if (part[index] == '~' && (++index >= part.Length || part[index] is not ('0' or '1')))
                    throw Invalid("A JSON Pointer contains an invalid escape.");
            return part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }).ToArray();
    }

    private static void ValidatePaths(IReadOnlyList<ToolArgumentBinding> bindings)
    {
        if (bindings.Any(binding => binding is null || binding.Path is null
            || binding is ParameterToolArgumentBinding { Parameter: null } or SecretToolArgumentBinding { Secret: null }))
            throw Invalid("Each fixed input requires a path and a configured value source.");
        if (bindings.Count > MaximumBindings) throw Invalid("Too many fixed inputs.");
        if (bindings.Sum(binding => (long)binding.Path.Length + (binding is LiteralToolArgumentBinding literal ? literal.Value.GetRawText().Length : 0)) > MaximumPayloadLength)
            throw Invalid("Fixed inputs exceed the relay configuration size limit.");
        var paths = bindings.Select(value => Segments(value.Path)).ToArray();
        for (var left = 0; left < paths.Length; left++)
            for (var right = 0; right < left; right++)
                if (paths[left].Take(Math.Min(paths[left].Length, paths[right].Length))
                    .SequenceEqual(paths[right].Take(Math.Min(paths[left].Length, paths[right].Length)), StringComparer.Ordinal))
                    throw Invalid("Fixed inputs cannot duplicate or overlap parent and child paths.");
    }

    private static void Prune(JsonObject node, IReadOnlyList<string[]> paths)
    {
        if (paths.Count == 0) return;
        // These constraints cannot be projected by simply removing object properties.
        string[] unsupported = ["$ref", "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "dependentRequired", "dependentSchemas", "patternProperties", "minProperties", "maxProperties", "enum", "const"];
        if (unsupported.Any(node.ContainsKey)) throw Invalid("The source schema uses object constraints not supported by fixed-input projection.");
        if (node["properties"] is not JsonObject properties) throw Invalid("Fixed paths require declared object properties.");
        var required = node["required"] as JsonArray ?? new JsonArray();
        foreach (var group in paths.GroupBy(path => path[0], StringComparer.Ordinal))
        {
            var descendants = group.Select(path => path[1..]).ToArray();
            if (descendants.Any(path => path.Length == 0))
            {
                properties.Remove(group.Key);
                RemoveRequired(required, group.Key);
            }
            else
            {
                var child = properties[group.Key] as JsonObject ?? throw Invalid("A fixed parent must be an object property.");
                Prune(child, descendants);
                RemoveRequired(required, group.Key);
                // Reconstruction creates this object even when its original parent was optional.
                if (child["required"] is JsonArray { Count: > 0 }) required.Add(group.Key);
            }
        }
        node["required"] = required.DeepClone();
        // A fixed-input ancestor cannot accept undeclared aliases for hidden properties.
        node["additionalProperties"] = false;
    }

    private static void RejectUnsupportedConstraints(JsonElement schema, int depth)
    {
        if (depth > 12 || schema.ValueKind != JsonValueKind.Object) throw Invalid("Unsupported source schema shape or depth.");
        string[] unsupported = ["$ref", "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "dependentRequired", "dependentSchemas", "patternProperties", "unevaluatedProperties", "contains", "prefixItems", "const", "propertyNames", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "uniqueItems", "minProperties", "maxProperties"];
        if (unsupported.Any(keyword => schema.TryGetProperty(keyword, out _)))
            throw Invalid("The source contract uses constraints outside the supported relay schema profile.");
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
            throw Invalid("Schema-valued additional properties are not supported by relay validation.");
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject()) RejectUnsupportedConstraints(property.Value, depth + 1);
        if (schema.TryGetProperty("items", out var items)) RejectUnsupportedConstraints(items, depth + 1);
    }

    private static void RemoveRequired(JsonArray required, string name)
    {
        for (var index = required.Count - 1; index >= 0; index--)
            if (required[index]?.GetValue<string>() == name) required.RemoveAt(index);
    }

    private static void RejectDuplicateProperties(JsonElement value, int depth)
    {
        if (depth > 12) throw Invalid("Relay input exceeds the nesting limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid("Duplicate JSON property names are not permitted.");
                RejectDuplicateProperties(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item, depth + 1);
    }

    private static ToolDefinitionValidationException Invalid(string message) => new("tool_definition_binding_invalid", message);
}
