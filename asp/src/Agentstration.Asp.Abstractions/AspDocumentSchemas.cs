using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Asp.Abstractions;

public static class AspDocumentSchemas
{
    public static IReadOnlyList<string> Operations { get; } = ["describe", "get", "put", "delete", "query", "prepare", "seal", "inspect", "retire"];
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => For<AspDocumentInvocation<AspDescribeRequest>>(),
        "get" => For<AspDocumentInvocation<AspDocumentGet>>(),
        "put" => For<AspDocumentInvocation<AspDocumentPut>>(),
        "delete" => For<AspDocumentInvocation<AspDocumentDelete>>(),
        "query" => For<AspDocumentInvocation<AspDocumentQuery>>(),
        "prepare" => For<AspDocumentInvocation<AspDocumentPrepare>>(),
        "seal" or "inspect" or "retire" => For<AspDocumentInvocation<AspDocumentPublicationRequest>>(),
        _ => throw new AspException("unsupported_operation", "Unknown document operation.")
    };
    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => For<AspReply<AspDocumentDescription>>(),
        "get" or "put" => For<AspReply<AspDocument>>(),
        "delete" => For<AspReply<AspDocumentDeleted>>(),
        "query" => For<AspReply<AspDocumentPage>>(),
        "prepare" or "seal" or "inspect" or "retire" => For<AspReply<AspDocumentPublication>>(),
        _ => throw new AspException("unsupported_operation", "Unknown document operation.")
    };
    private static JsonElement For<T>() => JsonSerializer.SerializeToElement(AspProtocol.JsonOptions.GetJsonSchemaAsNode(typeof(T),
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
