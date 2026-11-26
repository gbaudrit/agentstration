using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Asc.Abstractions;

public static class AscDocumentSchemas
{
    public static IReadOnlyList<string> Operations { get; } = ["describe", "get", "put", "delete", "query", "prepare", "seal", "inspect", "retire"];
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => For<AscDocumentInvocation<AscDescribeRequest>>(),
        "get" => For<AscDocumentInvocation<AscDocumentGet>>(),
        "put" => For<AscDocumentInvocation<AscDocumentPut>>(),
        "delete" => For<AscDocumentInvocation<AscDocumentDelete>>(),
        "query" => For<AscDocumentInvocation<AscDocumentQuery>>(),
        "prepare" => For<AscDocumentInvocation<AscDocumentPrepare>>(),
        "seal" or "inspect" or "retire" => For<AscDocumentInvocation<AscDocumentPublicationRequest>>(),
        _ => throw new AscException("unsupported_operation", "Unknown document operation.")
    };
    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => For<AscReply<AscDocumentDescription>>(),
        "get" or "put" => For<AscReply<AscDocument>>(),
        "delete" => For<AscReply<AscDocumentDeleted>>(),
        "query" => For<AscReply<AscDocumentPage>>(),
        "prepare" or "seal" or "inspect" or "retire" => For<AscReply<AscDocumentPublication>>(),
        _ => throw new AscException("unsupported_operation", "Unknown document operation.")
    };
    private static JsonElement For<T>() => JsonSerializer.SerializeToElement(AscContract.JsonOptions.GetJsonSchemaAsNode(typeof(T),
        new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject obj && obj.ContainsKey("properties")) obj["additionalProperties"] = false;
                return node;
            }
        }));
}
