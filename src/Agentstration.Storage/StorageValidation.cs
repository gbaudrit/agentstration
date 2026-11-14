using System.Text.Json;
using Agentstration.Schemas;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

public sealed class StorageException(string code, string message, int statusCode = 422) : Exception(message)
{
    public IReadOnlyList<StorageValidationIssue> ValidationIssues { get; init; } = [];
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal static class StorageValidation
{
    public const int MaximumJsonBytes = 256 * 1024;
    private const int MaximumDepth = 16;

    public static void Json(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumJsonBytes)
            throw new StorageException("storage_json_invalid", "JSON documents must be objects of at most 256 KiB.");
        CheckDepth(value, 0);
    }

    private static void CheckDepth(JsonElement value, int depth)
    {
        if (depth > MaximumDepth)
            throw new StorageException("storage_json_too_deep", "JSON documents cannot exceed 16 nested levels.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new StorageException("storage_json_duplicate", "JSON property names must be unique.");
                CheckDepth(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDepth(item, depth + 1);
    }

    public static async Task SchemaAsync(JsonElement schema, CancellationToken token)
    {
        Json(schema);
        var result = await new BuiltinJsonSchemaFormatHandler().ValidateDefinitionAsync(schema, null, token);
        if (!result.IsValid)
            throw new StorageException("storage_schema_invalid", "The schema is outside the supported bounded JSON Schema profile.");
    }

    public static async Task InstanceAsync(JsonElement schema, JsonElement value, CancellationToken token)
    {
        Json(value);
        var result = await new BuiltinJsonSchemaFormatHandler().ValidateInstanceAsync(schema, value, null, token);
        if (!result.IsValid)
            throw new StorageException("storage_configuration_invalid", "The configuration or envelope does not satisfy its pinned schema.");
    }

    public static void Name(string name)
    {
        if (name is not { Length: >= 1 and <= 128 } || !char.IsAsciiLetterOrDigit(name[0])
            || name.Any(c => c is not (>= 'a' and <= 'z') && !char.IsAsciiDigit(c) && c is not '-' and not '.'))
            throw new StorageException("storage_name_invalid", "Names require 1 to 128 lowercase letters, digits, '.' or '-', starting with a letter or digit.");
    }

    public static void Display(string? display, string? description)
    {
        if (string.IsNullOrWhiteSpace(display) || display.Length > 200 || description?.Length > 2000)
            throw new StorageException("storage_metadata_invalid", "Display names require 1 to 200 characters and descriptions at most 2000.");
    }

    public static void MemberId(string? id)
    {
        if (id is not { Length: >= 1 and <= 64 } || id[0] is < 'a' or > 'z'
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new StorageException("storage_requirement_id_invalid",
                "Requirement identifiers require 1 to 64 ASCII letters, digits, '.', '_' or '-', starting with a lowercase letter.");
    }

    public static void ETag(string? expected, string current)
    {
        if (string.IsNullOrWhiteSpace(expected))
            throw new StorageException("storage_etag_required", "If-Match is required.", 428);
        if (!string.Equals(expected, current, StringComparison.Ordinal))
            throw new StorageException("storage_version_conflict", "The resource version has changed.", 409);
    }
}
