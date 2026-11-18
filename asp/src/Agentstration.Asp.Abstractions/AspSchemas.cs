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
        "read_blob" => Transfer<AspReadBlobRequest>(false),
        "read_text" => Transfer<AspReadTextRequest>(false),
        "write_blob" => Transfer<AspWriteBlobRequest>(true),
        "write_text" => Transfer<AspWriteTextRequest>(true),
        "close_read" or "abort_write" or "write_status" => For<AspInvocation<AspSessionRequest>>(),
        _ => throw new AspException("unsupported_operation", "Unknown ASP operation mapping.")
    };

    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => ForReply<AspDescription>(),
        "list" => ForReply<AspListResult>(),
        "stat" => ForReply<AspObject>(),
        "read_blob" => ForReply<AspReadBlobResult>(),
        "read_text" => ForReply<AspReadTextResult>(),
        "write_blob" or "write_text" => ForReply<AspWriteResult>(),
        "close_read" or "abort_write" or "write_status" => ForReply<AspSessionResult>(),
        _ => throw new AspException("unsupported_operation", "Unknown ASP operation mapping.")
    };

    private static JsonElement Transfer<T>(bool writing)
    {
        var schema = JsonNode.Parse(For<AspInvocation<T>>().GetRawText())!.AsObject();
        var arguments = schema["properties"]!["arguments"]!.AsObject();
        var fixedField = writing ? "options" : "selection";
        arguments["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["required"] = new JsonArray(fixedField, "notAfter"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("sessionRef") },
                ["properties"] = new JsonObject
                {
                    ["sequence"] = new JsonObject { ["const"] = 0 },
                    [fixedField] = new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } },
                    ["notAfter"] = new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } }
                }
            },
            new JsonObject
            {
                ["required"] = new JsonArray("sessionRef", "sequence"),
                ["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                    new JsonObject { ["required"] = new JsonArray(fixedField) },
                    new JsonObject { ["required"] = new JsonArray("notAfter") },
                    new JsonObject { ["required"] = new JsonArray("encoding") })
                },
                ["properties"] = new JsonObject { ["sessionRef"] = new JsonObject { ["type"] = "string" } }
            });
        return JsonSerializer.SerializeToElement(schema);
    }

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
                        case "requestId":
                        case "version":
                        case "providerId":
                        case "providerVersion":
                        case "storeId":
                        case "bindingId":
                        case "revision":
                            obj["minLength"] = 1; obj["maxLength"] = 128; break;
                        case "key": obj["minLength"] = 1; obj["maxLength"] = 256; break;
                        case "prefix": obj["maxLength"] = 256; break;
                        case "reference": case "cursor": case "sessionRef": obj["minLength"] = 1; obj["maxLength"] = 4096; break;
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
