using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Tools;

public static class ToolDryRunContract
{
    public const string ParameterName = "dryRun";

    public static bool IsSupported(JsonElement? inputSchema)
    {
        if (inputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(ParameterName, out var property)
            || property.ValueKind != JsonValueKind.Object
            || !property.TryGetProperty("type", out var type))
            return false;

        return type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "boolean", StringComparison.Ordinal);
    }

    public static JsonElement Enable(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be a JSON object to enable dry run.", nameof(arguments));

        var values = JsonNode.Parse(arguments.GetRawText())?.AsObject()
            ?? throw new ArgumentException("Tool arguments must be a JSON object to enable dry run.", nameof(arguments));
        values[ParameterName] = true;
        return JsonSerializer.SerializeToElement(values);
    }

    public static bool IsEnabled(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(ParameterName, out var value)
        && value.ValueKind == JsonValueKind.True;
}
