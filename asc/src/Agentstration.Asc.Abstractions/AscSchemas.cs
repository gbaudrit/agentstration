using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Asc.Abstractions;

// The executable schema is shared by server publication and client compatibility checks.
public static class AscSchemas
{
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => For<AscInvocation<AscDescribeRequest>>(),
        "list" => For<AscInvocation<AscListRequest>>(),
        "stat" => For<AscInvocation<AscReferenceRequest>>(),
        "read_blob" => Transfer<AscReadBlobRequest>(false),
        "read_text" => Transfer<AscReadTextRequest>(false),
        "write_blob" => Transfer<AscWriteBlobRequest>(true),
        "write_text" => Transfer<AscWriteTextRequest>(true),
        "close_read" or "abort_write" or "write_status" => For<AscInvocation<AscSessionRequest>>(),
        _ => throw new AscException("unsupported_operation", "Unknown ASC operation mapping.")
    };

    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => ForReply<AscDescription>(),
        "list" => ForReply<AscListResult>(),
        "stat" => ForReply<AscObject>(),
        "read_blob" => ForReply<AscReadBlobResult>(),
        "read_text" => ForReply<AscReadTextResult>(),
        "write_blob" or "write_text" => ForReply<AscWriteResult>(),
        "close_read" or "abort_write" or "write_status" => ForReply<AscSessionResult>(),
        _ => throw new AscException("unsupported_operation", "Unknown ASC operation mapping.")
    };

    private static JsonElement Transfer<T>(bool writing)
    {
        var schema = JsonNode.Parse(For<AscInvocation<T>>().GetRawText())!.AsObject();
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
        var schema = JsonNode.Parse(For<AscReply<T>>().GetRawText())!.AsObject();
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
        var schema = AscContract.JsonOptions.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
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
                        case "limit": obj["minimum"] = 1; obj["maximum"] = AscContract.MaxPageSize; break;
                        case "maxBytes": obj["minimum"] = 1; obj["maximum"] = AscContract.MaxContentBytes; break;
                        case "offset": case "length": obj["minimum"] = 0; break;
                        case "contentBase64": obj["contentEncoding"] = "base64"; obj["maxLength"] = 87384; break;
                        case "ascVersion": obj["const"] = AscContract.Version; break;
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
