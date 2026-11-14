using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Extensions.OpenApi;

// Project only contracts the native acquisition validator can enforce, without inventing required values.
internal static class OpenApiAcquisitionSchemas
{
    public static (JsonElement Input, JsonElement Output) Create(ParsedOpenApiContract contract, OpenApiOperationDescriptor operation, string? responseStatus = null, string? responseMediaType = null)
    {
        var document = contract.Document;
        var path = Resolve(document["paths"]?[operation.Path]) as JsonObject ?? throw Unsupported();
        var endpoint = path[operation.Method.ToLowerInvariant()] as JsonObject ?? throw Unsupported();
        var input = Object();
        var groups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var parameters = new Dictionary<(string Location, string Name), JsonObject>();
        foreach (var array in new[] { path["parameters"] as JsonArray, endpoint["parameters"] as JsonArray })
            foreach (var node in array ?? [])
            {
                var parameter = Resolve(node) as JsonObject ?? throw Unsupported();
                var location = parameter["in"]?.GetValue<string>() ?? throw Unsupported();
                var name = parameter["name"]?.GetValue<string>() ?? throw Unsupported();
                parameters[(location, name)] = parameter; // Operation parameters override the same path-level parameter.
            }
        foreach (var ((location, name), parameter) in parameters)
        {
            var groupName = location switch { "path" => "path", "query" => "query", "header" => "headers", _ => throw Unsupported() };
            if (location == "header" && (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                throw Unsupported();
            var schema = Schema(parameter["schema"], 0);
            if (schema["type"]?.GetValueKind() != JsonValueKind.String
                || schema["type"]!.GetValue<string>() is not ("string" or "number" or "integer" or "boolean")) throw Unsupported();
            if (!groups.TryGetValue(groupName, out var group)) groups[groupName] = group = Object();
            ((JsonObject)group["properties"]!)[name] = schema;
            if (location == "path" || parameter["required"]?.GetValue<bool>() == true)
                ((JsonArray)group["required"]!).Add(name);
        }
        foreach (var (name, group) in groups)
        {
            ((JsonObject)input["properties"]!)[name] = group;
            if (((JsonArray)group["required"]!).Count > 0) ((JsonArray)input["required"]!).Add(name);
        }
        if (Resolve(endpoint["requestBody"]) is JsonObject body)
        {
            ((JsonObject)input["properties"]!)["body"] = Schema(body["content"]?["application/json"]?["schema"], 0);
            if (body["required"]?.GetValue<bool>() == true) ((JsonArray)input["required"]!).Add("body");
        }
        var responses = Resolve(endpoint["responses"]) as JsonObject ?? throw Unsupported();
        JsonObject output;
        if (responseStatus is not null && responseMediaType is not null)
        {
            var content = (Resolve(responses[responseStatus]) as JsonObject)?["content"] as JsonObject;
            var media = content?.FirstOrDefault(p => p.Key.Equals(responseMediaType, StringComparison.OrdinalIgnoreCase)).Value;
            output = Schema(media?["schema"], 0);
        }
        else
        {
            var schemas = responses.Where(item => item.Key.Length == 3 && item.Key[0] == '2')
                .Select(item => Schema((Resolve(item.Value) as JsonObject)?["content"]?["application/json"]?["schema"], 0)).ToArray();
            output = schemas.FirstOrDefault() ?? Schema((Resolve(responses["default"]) as JsonObject)?["content"]?["application/json"]?["schema"], 0);
            if (schemas.Any(schema => !JsonNode.DeepEquals(schema, output))) throw Unsupported();
        }
        return (JsonSerializer.SerializeToElement(input), JsonSerializer.SerializeToElement(output));

        JsonNode? Resolve(JsonNode? node)
        {
            for (var depth = 0; node is JsonObject obj && obj["$ref"] is JsonValue reference; depth++)
            {
                if (depth > 12 || !reference.GetValue<string>().StartsWith("#/", StringComparison.Ordinal)
                    || obj.Any(item => item.Key != "$ref")) throw Unsupported();
                node = document;
                foreach (var segment in reference.GetValue<string>()[2..].Split('/'))
                    node = node?[Uri.UnescapeDataString(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
            }
            return node;
        }
        JsonObject Schema(JsonNode? node, int depth)
        {
            if (depth > 12) throw Unsupported();
            if (node is null) return AnyJson();
            if (Resolve(node) is not JsonObject source) throw Unsupported();
            var result = new JsonObject();
            foreach (var (key, value) in source)
            {
                if (key == "properties" && value is JsonObject properties)
                {
                    var children = new JsonObject();
                    foreach (var child in properties) children[child.Key] = Schema(child.Value, depth + 1);
                    result[key] = children;
                }
                else if (key == "items") result[key] = Schema(value, depth + 1);
                else if (key is "type" or "required" or "additionalProperties" or "enum" or "minItems" or "maxItems"
                    or "minLength" or "maxLength" or "pattern" or "format" or "minimum" or "maximum"
                    or "title" or "description" or "default" or "examples") result[key] = value?.DeepClone();
                else throw Unsupported();
            }
            if (result.Count == 0) return AnyJson();
            if (!result.ContainsKey("type") || result["additionalProperties"] is JsonObject) throw Unsupported();
            return result;
        }
    }
    private static JsonObject Object() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false };
    private static JsonObject AnyJson() => new() { ["type"] = new JsonArray("object", "array", "string", "number", "boolean", "null") };
    private static OpenApiExtensionException Unsupported() => new("openapi_acquisition_schema_unsupported", "The operation uses an unsupported acquisition schema.");
}
