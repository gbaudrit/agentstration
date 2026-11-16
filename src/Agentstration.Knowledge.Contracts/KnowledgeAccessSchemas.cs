using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeAccessSchemas
{
    public const string DescribeTool = "knowledge.content.describe";
    public const string DocumentQueryTool = "knowledge.documents.query";
    public const string ReadTool = "knowledge.content.read";
    public const string RegexTool = "knowledge.content.search-regex";
    public const string StatusTool = "knowledge.content.get-status";
    public static string Route(KnowledgeSourceOperation operation) => operation switch
    {
        KnowledgeSourceOperation.Describe => "describe",
        KnowledgeSourceOperation.StructuredQuery => "query_documents",
        KnowledgeSourceOperation.Read => "read",
        KnowledgeSourceOperation.SearchRegex => "search_regex",
        KnowledgeSourceOperation.GetStatus => "get_status",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
    public static string ToolName(KnowledgeSourceOperation operation) => operation switch
    {
        KnowledgeSourceOperation.Describe => DescribeTool,
        KnowledgeSourceOperation.StructuredQuery => DocumentQueryTool,
        KnowledgeSourceOperation.Read => ReadTool,
        KnowledgeSourceOperation.SearchRegex => RegexTool,
        KnowledgeSourceOperation.GetStatus => StatusTool,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
    public static JsonElement Input(KnowledgeSourceOperation operation)
    {
        var properties = new JsonObject
        {
            ["snapshotId"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["minLength"] = 1,
                ["maxLength"] = 256,
                ["description"] = "Omit or use null for the current Snapshot. To select or compare a specific Snapshot, use a non-empty snapshotId returned by Knowledge access; an empty string is invalid."
            }
        };
        if (operation == KnowledgeSourceOperation.Describe)
        {
            properties["exposure"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 };
            properties["exposureScope"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 };
        }
        if (operation is KnowledgeSourceOperation.Read or KnowledgeSourceOperation.SearchRegex or KnowledgeSourceOperation.StructuredQuery)
            properties["contribution"] = JsonNode.Parse("""{"type":"object","properties":{"bindingName":{"type":"string","maxLength":200},"dataSourceUid":{"type":"string","format":"uuid"}},"required":["bindingName","dataSourceUid"],"additionalProperties":false}""");
        if (operation is not (KnowledgeSourceOperation.GetStatus or KnowledgeSourceOperation.Describe))
        {
            properties["cursor"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 16384 };
            properties["preparationId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 16384 };
        }
        if (operation == KnowledgeSourceOperation.Read)
        {
            properties["documentId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 256 };
            properties["maxCharacters"] = Integer(1, 32768);
            properties["range"] = new JsonObject
            {
                ["type"] = new JsonArray("object", "null"),
                ["properties"] = new JsonObject
                { ["start"] = Integer(0, 1048576), ["end"] = Integer(0, 1048576) },
                ["required"] = new JsonArray("start", "end"),
                ["additionalProperties"] = false
            };
        }
        if (operation == KnowledgeSourceOperation.SearchRegex)
        {
            properties["pattern"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["minLength"] = 1, ["maxLength"] = 512 };
            properties["ignoreCase"] = new JsonObject { ["type"] = "boolean" };
            properties["limit"] = Integer(1, 100);
            properties["contextCharacters"] = Integer(0, 1000);
        }
        if (operation == KnowledgeSourceOperation.StructuredQuery)
        {
            properties["query"] = new JsonObject();
            properties["pageSize"] = Integer(1, 100);
            properties["pageSize"]!["type"] = new JsonArray("integer", "null");
            properties["profile"] = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"},"version":{"type":"string"},"encoding":{"type":"string"},"modelDigest":{"type":"string"}},"required":["id","version","encoding","modelDigest"],"additionalProperties":false}""");
        }
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        });
    }
    public static JsonElement Envelope(KnowledgeSourceOperation operation) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source"] = JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"},"namespace":{"type":"string"},"scopeRef":{"type":"string"},"uid":{"type":"string","format":"uuid"}},"required":["name","namespace","scopeRef","uid"],"additionalProperties":false}"""),
            ["request"] = JsonNode.Parse(Input(operation).GetRawText())
        },
        ["required"] = new JsonArray("source", "request"),
        ["additionalProperties"] = false
    });
    private static JsonObject Integer(int minimum, int maximum) => new()
    { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
}
