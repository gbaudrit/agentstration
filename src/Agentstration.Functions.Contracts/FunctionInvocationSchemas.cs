using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Functions.Contracts;

public static class FunctionInvocationSchemas
{
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
