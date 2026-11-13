using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentstration.Flows.Application;

/// <summary>Conservative compatibility for the bounded JSON contracts used by sequential composition.</summary>
public static class FlowCompositionSchema
{
    public const int MaximumParametersLength = 65_536;
    public const int MaximumValueLength = 1_048_576;

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
    public static void ValidateValue(JsonElement schema, JsonElement value, int depth = 0)
    {
        if (depth > 12 || value.ValueKind == JsonValueKind.Undefined || !IsSupported(schema)) throw Invalid();
        if (value.GetRawText().Length > MaximumValueLength)
            throw new FlowValidationException("flow_composition_value_too_large", $"A composition value cannot exceed {MaximumValueLength} JSON characters.");
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type)) throw Invalid();
        var valid = type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
        if (!valid) throw Invalid();
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))) throw Invalid();
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("pattern", out var pattern))
                try { if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Invalid(); }
                catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException) { throw Invalid(); }
            if (schema.TryGetProperty("format", out var format) && !(format.GetString() switch
            {
                "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
                "uuid" => Guid.TryParse(text, out _),
                "date-time" => DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _),
                _ => false
            })) throw Invalid();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)
                && required.EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out _))) throw Invalid();
            foreach (var property in value.EnumerateObject())
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child)) ValidateValue(child, property.Value, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False) throw Invalid();
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) ValidateValue(items, item, depth + 1);
        foreach (var pair in new[] { ("minLength", "maxLength", value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : -1),
            ("minItems", "maxItems", value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : -1) })
            if (pair.Item3 >= 0 && (schema.TryGetProperty(pair.Item1, out var min) && pair.Item3 < min.GetInt32()
                || schema.TryGetProperty(pair.Item2, out var max) && pair.Item3 > max.GetInt32())) throw Invalid();
        if (value.ValueKind == JsonValueKind.Number && (schema.TryGetProperty("minimum", out var lower) && value.GetDecimal() < lower.GetDecimal()
            || schema.TryGetProperty("maximum", out var upper) && value.GetDecimal() > upper.GetDecimal())) throw Invalid();
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

    private static FlowValidationException Invalid() => new("flow_composition_value_invalid", "A composition value does not satisfy its bounded contract.");
}
