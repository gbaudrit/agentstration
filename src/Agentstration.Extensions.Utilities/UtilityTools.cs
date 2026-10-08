using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.Utilities;

[McpServerToolType]
public static class UtilityTools
{
    [McpServerTool(Name = "hash_compute"), Description("Compute a lowercase SHA-256 digest for UTF-8 text.")]
    public static string ComputeHash(string text, [Description("Runs the Tool without side effects.")] bool dryRun = false)
    {
        _ = dryRun;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [McpServerTool(Name = "json_compact"), Description("Normalize a JSON value to a compact representation.")]
    public static string CompactJson(string json, [Description("Runs the Tool without side effects.")] bool dryRun = false)
    {
        _ = dryRun;
        return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));
    }

    [McpServerTool(Name = "text_upper"), Description("Convert text to invariant uppercase.")]
    public static string Uppercase(string text, [Description("Runs the Tool without side effects.")] bool dryRun = false)
    {
        _ = dryRun;
        return text.ToUpperInvariant();
    }
}
