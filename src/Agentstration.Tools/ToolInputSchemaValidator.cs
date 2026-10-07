using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentstration.Tools;

public sealed class ToolInputValidationException(string code, string path, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

public static partial class ToolInputSchemaValidator
{
    private const int MaximumDepth = 12;
    private const int MaximumPayloadLength = 65_536;

    public static void Validate(JsonElement? schema, JsonElement arguments)
    {
        if (arguments.GetRawText().Length > MaximumPayloadLength)
            throw Error("tool_arguments_too_large", "$", $"Tool arguments cannot exceed {MaximumPayloadLength} JSON characters.");
        if (schema is null || schema.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
                throw Error("tool_arguments_object_required", "$", "Tool arguments must be a JSON object.");
            return;
        }
        ValidateNode(schema.Value, arguments, "$", 0);
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > MaximumDepth)
            throw Error("tool_schema_too_deep", path, "The Tool input schema exceeds the supported nesting depth.");
        if (schema.ValueKind != JsonValueKind.Object)
            throw Error("tool_input_schema_invalid", path, "The Tool input schema must contain JSON objects.");

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            throw Error("tool_argument_enum_invalid", path, $"Value at '{path}' is not one of the allowed values.");

        var type = Text(schema, "type") ?? (schema.TryGetProperty("properties", out _) ? "object" : null);
        if (type is not null && !Matches(value, type))
            throw Error("tool_argument_type_invalid", path, $"Value at '{path}' does not match schema type '{type}'.");

        switch (type)
        {
            case "object":
                ValidateObject(schema, value, path, depth);
                break;
            case "array":
                ValidateArray(schema, value, path, depth);
                break;
            case "string":
                ValidateString(schema, value.GetString()!, path);
                break;
            case "integer":
            case "number":
                ValidateNumber(schema, value, path);
                break;
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
            {
                var name = item.GetString()!;
                if (!value.TryGetProperty(name, out _))
                    throw Error("tool_argument_required", PropertyPath(path, name), $"Tool argument '{name}' is required.");
            }
        }

        var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out var propertySchema))
            {
                if (rejectAdditional)
                    throw Error("tool_argument_unknown", PropertyPath(path, property.Name), $"Tool argument '{property.Name}' is not declared by the Tool schema.");
                continue;
            }
            ValidateNode(propertySchema, property.Value, PropertyPath(path, property.Name), depth + 1);
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (value.ValueKind != JsonValueKind.Array) return;
        CheckCount(value.GetArrayLength(), Integer(schema, "minItems"), Integer(schema, "maxItems"), path, "items");
        if (!schema.TryGetProperty("items", out var items)) return;
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateNode(items, item, $"{path}[{index}]", depth + 1);
            index++;
        }
    }

    private static void ValidateString(JsonElement schema, string value, string path)
    {
        CheckCount(value.Length, Integer(schema, "minLength"), Integer(schema, "maxLength"), path, "characters");
        if (Text(schema, "pattern") is { Length: > 0 } pattern)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    throw Error("tool_argument_pattern_invalid", path, $"Value at '{path}' does not match the expected pattern.");
            }
            catch (ArgumentException exception)
            {
                throw Error("tool_input_schema_invalid", path, $"The Tool input pattern at '{path}' is invalid: {exception.Message}");
            }
            catch (RegexMatchTimeoutException)
            {
                throw Error("tool_argument_pattern_timeout", path, $"Pattern validation timed out at '{path}'.");
            }
        }
        if (string.Equals(Text(schema, "format"), "uri", StringComparison.OrdinalIgnoreCase)
            && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Scheme)))
            throw Error("tool_argument_format_invalid", path, $"Value at '{path}' must be an absolute URI.");
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        if (!value.TryGetDecimal(out var number)) return;
        if (Decimal(schema, "minimum") is { } minimum && number < minimum)
            throw Error("tool_argument_minimum", path, $"Value at '{path}' must be at least {minimum.ToString(CultureInfo.InvariantCulture)}.");
        if (Decimal(schema, "maximum") is { } maximum && number > maximum)
            throw Error("tool_argument_maximum", path, $"Value at '{path}' must be at most {maximum.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static void CheckCount(int value, int? minimum, int? maximum, string path, string unit)
    {
        if (minimum is { } min && value < min)
            throw Error("tool_argument_minimum_length", path, $"Value at '{path}' must contain at least {min} {unit}.");
        if (maximum is { } max && value > max)
            throw Error("tool_argument_maximum_length", path, $"Value at '{path}' must contain at most {max} {unit}.");
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string PropertyPath(string path, string property) => $"{path}.{property}";
    private static string? Text(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static decimal? Decimal(JsonElement schema, string name) => schema.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : null;
    private static ToolInputValidationException Error(string code, string path, string message) => new(code, path, message);
}
