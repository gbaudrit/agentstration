using System.Text.Json;

namespace Agentstration.Flows;

/// <summary>Conservative structural assignability shared by composition and the Designer. Unsupported schemas require runtime validation.</summary>
public static class FlowSchemaCompatibility
{
    public static bool IsAssignable(JsonElement output, JsonElement input, int depth = 0)
    {
        if (depth > 12 || !IsSupported(output) || !IsSupported(input)) return false;
        if (!input.TryGetProperty("type", out var wanted) || !output.TryGetProperty("type", out var actual)) return false;
        if (!JsonElement.DeepEquals(actual, wanted) && !(actual.GetString() == "integer" && wanted.GetString() == "number")) return false;
        if (input.TryGetProperty("enum", out var allowed)
            && (!output.TryGetProperty("enum", out var produced) || produced.EnumerateArray().Any(value => !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))))) return false;
        foreach (var bound in new[] { "minimum", "minLength", "minItems" })
            if (input.TryGetProperty(bound, out var min) && (!output.TryGetProperty(bound, out var value) || value.GetDecimal() < min.GetDecimal())) return false;
        foreach (var bound in new[] { "maximum", "maxLength", "maxItems" })
            if (input.TryGetProperty(bound, out var max) && (!output.TryGetProperty(bound, out var value) || value.GetDecimal() > max.GetDecimal())) return false;
        foreach (var constraint in new[] { "pattern", "format" })
            if (input.TryGetProperty(constraint, out var expected)
                && (!output.TryGetProperty(constraint, out var constrained) || !JsonElement.DeepEquals(expected, constrained))) return false;
        if (wanted.GetString() == "array" && input.TryGetProperty("items", out var items))
            return output.TryGetProperty("items", out var producedItems) && IsAssignable(producedItems, items, depth + 1);
        if (wanted.GetString() != "object") return true;
        var required = input.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
        var guaranteed = output.TryGetProperty("required", out var given) ? given.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        if (required.Any(name => !guaranteed.Contains(name))) return false;
        if (input.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                if (output.TryGetProperty("properties", out var available) && available.TryGetProperty(property.Name, out var value))
                { if (!IsAssignable(value, property.Value, depth + 1)) return false; }
                else if (required.Contains(property.Name, StringComparer.Ordinal)
                    || !output.TryGetProperty("additionalProperties", out var outputExtra) || outputExtra.ValueKind != JsonValueKind.False) return false;
        if (input.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
        {
            if (!output.TryGetProperty("additionalProperties", out var producedExtra) || producedExtra.ValueKind != JsonValueKind.False) return false;
            if (output.TryGetProperty("properties", out var available))
                foreach (var property in available.EnumerateObject())
                    if (!input.TryGetProperty("properties", out var accepted) || !accepted.TryGetProperty(property.Name, out _)) return false;
        }
        return true;
    }
    public static bool IsSupported(JsonElement schema, int depth = 0)
    {
        if (depth > 12 || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String || type.GetString() is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null")) return false;
        foreach (var property in schema.EnumerateObject())
        {
            switch (property.Name)
            {
                case "properties":
                    if (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any(child => !IsSupported(child.Value, depth + 1))) return false;
                    break;
                case "items": if (!IsSupported(property.Value, depth + 1)) return false; break;
                case "additionalProperties": if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; break;
                case "required": if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return false; break;
                case "enum": if (property.Value.ValueKind != JsonValueKind.Array) return false; break;
                case "minimum":
                case "maximum":
                case "minLength":
                case "maxLength":
                case "minItems":
                case "maxItems":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out _)) return false; break;
                case "pattern": if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024) return false; break;
                case "format": if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not ("uri" or "uuid" or "date-time")) return false; break;
                case "type": case "title": case "description": case "$schema": case "default": case "examples": break;
                default: return false;
            }
        }
        return true;
    }

}
