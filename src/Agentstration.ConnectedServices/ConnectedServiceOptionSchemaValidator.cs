using System.Text.Json;

namespace Agentstration.ConnectedServices;

internal static class ConnectedServiceOptionSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonElement value, JsonElement schema, string path = "options")
    {
        var issues = new List<string>();
        ValidateValue(value, schema, path, issues);
        return issues;
    }

    private static void ValidateValue(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            issues.Add($"Value at '{path}' is not one of the supported values.");
            return;
        }
        var type = schema.TryGetProperty("type", out var typeValue)
            && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString()
            : null;
        if (type is not null && !MatchesType(value, type))
        {
            issues.Add($"Value at '{path}' must be of type '{type}'.");
            return;
        }
        if (value.ValueKind == JsonValueKind.Object) ValidateObject(value, schema, path, issues);
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateValue(item, itemSchema, $"{path}[{index++}]", issues);
        }
        if (value.ValueKind == JsonValueKind.Number) ValidateNumber(value, schema, path, issues);
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> issues)
    {
        var properties = schema.TryGetProperty("properties", out var propertySchemas)
            && propertySchemas.ValueKind == JsonValueKind.Object
            ? propertySchemas
            : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                    issues.Add($"Required option '{name}' is missing.");
            }
        }
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(property.Value, propertySchema, $"{path}.{property.Name}", issues);
                continue;
            }
            if (schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False)
                issues.Add($"Option '{property.Name}' is not supported.");
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> issues)
    {
        var number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out var minimum)
            && minimum.ValueKind == JsonValueKind.Number
            && number < minimum.GetDouble())
            issues.Add($"Value at '{path}' must be at least {minimum.GetRawText()}.");
        if (schema.TryGetProperty("maximum", out var maximum)
            && maximum.ValueKind == JsonValueKind.Number
            && number > maximum.GetDouble())
            issues.Add($"Value at '{path}' must be at most {maximum.GetRawText()}.");
        if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum)
            && exclusiveMinimum.ValueKind == JsonValueKind.Number
            && number <= exclusiveMinimum.GetDouble())
            issues.Add($"Value at '{path}' must be greater than {exclusiveMinimum.GetRawText()}.");
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
