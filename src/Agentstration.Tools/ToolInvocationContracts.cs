using System.Text.Json;

namespace Agentstration.Tools;

/// <summary>Preserve the MCP result envelope without changing the provider-owned structured-content schema.</summary>
public static class ToolInvocationContracts
{
    public static JsonElement? OutputSchema(JsonElement? declared, ToolProviderProperties provider)
    {
        if (declared is null || provider.Mcp?.Internal == true) return declared?.Clone();
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["structuredContent"] = declared.Value,
                ["content"] = new { type = "array" },
                ["isError"] = new { type = "boolean" },
                ["_meta"] = new { type = "object" }
            },
            required = new[] { "structuredContent" }
        });
    }
}
