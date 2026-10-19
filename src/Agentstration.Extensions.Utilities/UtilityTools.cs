using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.Utilities;

[McpServerToolType]
public static class UtilityTools
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

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

    [McpServerTool(Name = "schema_validate_definition"),
     Description("Validate a JSON Schema definition for the sample AEP schema-format contribution.")]
    public static AepSchemaValidationResult ValidateSchemaDefinition(JsonElement definition, string? dialect = null)
    {
        var issues = new List<AepSchemaValidationIssue>();
        if (dialect is not null && !string.Equals(dialect, Draft202012, StringComparison.Ordinal))
            issues.Add(new("$schema", "unsupported_dialect", $"Dialect '{dialect}' is not supported."));
        if (definition.ValueKind != JsonValueKind.Object)
            issues.Add(new("$", "definition_type", "A JSON Schema definition must be an object."));
        else if (definition.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String || !SupportedType(type.GetString())))
            issues.Add(new("$.type", "unsupported_type", "The sample validator does not support this type."));
        return new(issues);
    }

    [McpServerTool(Name = "schema_validate_instance"),
     Description("Validate an instance for the sample AEP schema-format contribution.")]
    public static AepSchemaValidationResult ValidateSchemaInstance(
        JsonElement definition,
        JsonElement instance,
        string? dialect = null)
    {
        var definitionResult = ValidateSchemaDefinition(definition, dialect);
        if (definitionResult.Issues.Count > 0) return definitionResult;
        if (!definition.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return new([]);
        var expected = type.GetString();
        if (Matches(expected, instance)) return new([]);
        return new([new("$", "instance_type", $"The instance must have type '{expected}'.")]);
    }

    private static bool SupportedType(string? type) => type is
        "object" or "array" or "string" or "number" or "integer" or "boolean" or "null";

    private static bool Matches(string? type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
