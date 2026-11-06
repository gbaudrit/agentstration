using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Asp.Abstractions;

// The executable schema is shared by server publication and client compatibility checks.
public static class AspSchemas
{
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => For<AspInvocation<AspDescribeRequest>>(),
        "list" => For<AspInvocation<AspListRequest>>(),
        "stat" => For<AspInvocation<AspReferenceRequest>>(),
        "read" => For<AspInvocation<AspReadRequest>>(),
        "write" => For<AspInvocation<AspWriteRequest>>(),
        _ => throw new AspException("unsupported_operation", "Unknown ASP operation mapping.")
    };

    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => ForReply<AspDescription>(),
        "list" => ForReply<AspListResult>(),
        "stat" => ForReply<AspObject>(),
        "read" => ForReply<AspReadResult>(),
        "write" => ForReply<AspWriteResult>(),
        _ => throw new AspException("unsupported_operation", "Unknown ASP operation mapping.")
    };

    private static JsonElement ForReply<T>()
    {
        var schema = JsonNode.Parse(For<AspReply<T>>().GetRawText())!.AsObject();
        schema["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["required"] = new JsonArray("data"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("error") },
                ["properties"] = new JsonObject { ["data"] = new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } } }
            },
            new JsonObject
            {
                ["required"] = new JsonArray("error"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("data") },
                ["properties"] = new JsonObject { ["error"] = new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } } }
            });
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement For<T>()
    {
        var schema = AspProtocol.JsonOptions.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is JsonObject obj)
                {
                    if (obj.ContainsKey("properties")) obj["additionalProperties"] = false;
                    switch (context.PropertyInfo?.Name)
                    {
                        case "requestId": case "version": case "providerId": case "providerVersion":
                        case "storeId": case "bindingId": case "revision":
                            obj["minLength"] = 1; obj["maxLength"] = 128; break;
                        case "key": obj["minLength"] = 1; obj["maxLength"] = 256; break;
                        case "prefix": obj["maxLength"] = 256; break;
                        case "reference": case "cursor": obj["minLength"] = 1; obj["maxLength"] = 4096; break;
                        case "limit": obj["minimum"] = 1; obj["maximum"] = AspProtocol.MaxPageSize; break;
                        case "maxBytes": obj["minimum"] = 1; obj["maximum"] = AspProtocol.MaxContentBytes; break;
                        case "offset": case "length": obj["minimum"] = 0; break;
                        case "contentBase64": obj["contentEncoding"] = "base64"; obj["maxLength"] = 87384; break;
                        case "aspVersion": obj["const"] = AspProtocol.Version; break;
                        case "sha256": obj["pattern"] = "^[0-9a-f]{64}$"; break;
                    }
                }
                return node;
            }
        });
        schema.AsObject()["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return JsonSerializer.SerializeToElement(schema);
    }
}
