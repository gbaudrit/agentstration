using System.Text.Json;
using Json.Schema;

namespace Agentstration.Asp.Abstractions;

/// <summary>Explicit bounded JSON Schema subset, independent of product Schema resources.</summary>
public static class DocumentModel
{
    public static void ValidateSchema(JsonElement schema, int depth = 0)
    {
        if (depth == 0) schema = DocumentSchema.Normalize(schema);
        AspValidation.Require(depth <= 8 && schema.ValueKind == JsonValueKind.Object, "invalid_schema", "Invalid document schema shape.");
        AspDocumentValidation.ValidateJson(schema);
        if (schema.TryGetProperty("type", out var declared))
        {
            var kinds = declared.ValueKind == JsonValueKind.String ? new[] { declared.GetString() }
                : declared.ValueKind == JsonValueKind.Array ? declared.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() : null).ToArray() : [];
            AspValidation.Require(kinds.Length is 1 or 2 && kinds.All(k => k is "object" or "array" or "string" or "boolean" or "null" or "number" or "integer")
                && kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length && (kinds.Length == 1 || kinds.Contains("null", StringComparer.Ordinal)),
                "unsupported_schema", "Only a single JSON type and optional null are supported.");
        }
        foreach (var property in schema.EnumerateObject())
        {
            AspValidation.Require(property.Name is "type" or "properties" or "required" or "additionalProperties" or "items"
                or "minimum" or "maximum" or "minLength" or "maxLength" or "minItems" or "maxItems" or "enum"
                or "title" or "description" or "$schema" or "format", "unsupported_schema", "Unsupported document schema keyword.");
            if (property.Name == "properties")
            {
                AspValidation.Require(property.Value.ValueKind == JsonValueKind.Object && property.Value.EnumerateObject().Count() <= 64,
                    "invalid_schema", "Schema properties must be a bounded object.");
                foreach (var item in property.Value.EnumerateObject())
                {
                    AspValidation.Require(item.Name.Length is > 0 and <= 128, "invalid_schema", "Invalid schema property name.");
                    ValidateSchema(item.Value, depth + 1);
                }
            }
            if (property.Name == "required")
                AspValidation.Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 64
                    && property.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Length is > 0 and <= 128),
                    "invalid_schema", "Required properties must be bounded names.");
            if (property.Name == "enum") AspValidation.Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() is > 0 and <= 128,
                "invalid_schema", "An enumeration must be bounded and nonempty.");
            if (property.Name is "minimum" or "maximum") AspValidation.Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out _),
                "invalid_schema", "Numeric bounds must be supported numbers.");
            if (property.Name is "minLength" or "maxLength" or "minItems" or "maxItems")
                AspValidation.Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var bound) && bound is >= 0 and <= 65536,
                    "invalid_schema", "Schema size bounds must be bounded nonnegative integers.");
            if (property.Name is "title" or "description") AspValidation.Require(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length <= 4096,
                "invalid_schema", "Schema annotations must be bounded strings.");
            if (property.Name == "items") ValidateSchema(property.Value, depth + 1);
            if (property.Name == "additionalProperties") AspValidation.Require(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False, "unsupported_schema", "Only boolean additionalProperties is supported.");
            if (property.Name == "$schema") AspValidation.Require(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "https://json-schema.org/draft/2020-12/schema", "unsupported_schema", "Unsupported schema dialect.");
        }
        foreach (var (min, max) in new[] { ("minimum", "maximum"), ("minLength", "maxLength"), ("minItems", "maxItems") })
            if (schema.TryGetProperty(min, out var low) && schema.TryGetProperty(max, out var high))
                AspValidation.Require(low.GetDecimal() <= high.GetDecimal(), "invalid_schema", "Schema minimum must not exceed maximum.");
    }
    public static string? Type(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return null;
        return type.ValueKind == JsonValueKind.String ? type.GetString()
            : type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null");
    }
    public static void Validate(JsonElement schema, JsonElement value, int depth = 0)
    {
        AspValidation.Require(depth <= 8, "limit_exceeded", "Document schema depth exceeded.");
        var normalized = DocumentSchema.Normalize(schema);
        ValidateSchema(normalized);
        try
        {
            var compiled = JsonSchema.Build(normalized, new BuildOptions
            { SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => throw new AspException("unsupported_schema", "Remote schema resolution is forbidden.") } });
            var result = compiled.Evaluate(value, new EvaluationOptions { RequireFormatValidation = true });
            AspValidation.Require(result.IsValid, "schema_validation", "The document does not satisfy its declared schema.");
        }
        catch (JsonSchemaException)
        { throw new AspException("invalid_schema", "The document schema cannot be evaluated."); }
    }
}
