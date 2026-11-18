using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Functions.Contracts;

public static class FunctionInvocationSchemas
{
    public static JsonElement XmlContent { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"value":{"type":"string","maxLength":65536}},"required":["value"],"additionalProperties":false}
        """);
    // Declaration metadata belongs to the implementation contract, never to invocation values.
    public const string SchemaBindingKeyword = "x-agentstration-schema-binding";
    public static IReadOnlyList<string> SchemaParameters(JsonElement input) =>
        input.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Where(property => property.Value.TryGetProperty(SchemaBindingKeyword, out var marker)
                && marker.ValueKind == JsonValueKind.True).Select(property => property.Name).ToArray() : [];

    public static JsonElement WithoutSchemaParameters(JsonElement schema)
    {
        var result = JsonNode.Parse(schema.GetRawText())!.AsObject();
        foreach (var name in SchemaParameters(schema))
        {
            result["properties"]!.AsObject().Remove(name);
            if (result["required"] is JsonArray required)
                result["required"] = new JsonArray(required.Where(value => value?.GetValue<string>() != name).Select(value => value?.DeepClone()).ToArray());
        }
        return JsonSerializer.SerializeToElement(result);
    }

    public static JsonElement Parameters(JsonElement input)
    {
        if (!input.TryGetProperty("properties", out var propertiesSchema) || !propertiesSchema.TryGetProperty("value", out _))
            return JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false}""");
        var result = JsonNode.Parse(input.GetRawText())!.AsObject();
        result["properties"]!.AsObject().Remove("value");
        if (result["required"] is JsonArray required)
            result["required"] = new JsonArray(required.Where(value => value?.GetValue<string>() != "value").Select(value => value?.DeepClone()).ToArray());
        return JsonSerializer.SerializeToElement(result);
    }
}
