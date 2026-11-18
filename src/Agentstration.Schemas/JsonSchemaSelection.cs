using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentstration.Schemas;

public sealed class SchemaSelectionException(string code) : ArgumentException(code)
{
    public string Code { get; } = code;
}

/// <summary>Bounded local schema selection. Never fetches references or infers contracts from instances.</summary>
public static class JsonSchemaSelection
{
    public const int MaximumBytes = 65_536;

    public static JsonElement Select(JsonElement document, string? schemaPath = null)
    {
        try { return SelectCore(document, schemaPath); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            || exception is ArgumentException and not SchemaSelectionException)
        { throw Error("schema_selection_invalid"); }
    }

    private static JsonElement SelectCore(JsonElement document, string? schemaPath)
    {
        if (document.GetRawText().Length > 1_048_576) throw Error("schema_selection_too_large");
        var root = JsonNode.Parse(document.GetRawText()) ?? throw Error("schema_selection_invalid");
        var scanned = 0;
        var references = new HashSet<string>(StringComparer.Ordinal);
        CheckPolicy(root, 0);
        var selected = At(root, schemaPath ?? "", requireSchemaPath: true);
        var count = 0;
        var expandedBytes = 0;
        var output = Expand(selected, new HashSet<string>(StringComparer.Ordinal), 0);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(output);
        if (bytes.Length > MaximumBytes) throw Error("schema_selection_too_large");
        return JsonSerializer.Deserialize<JsonElement>(bytes);

        void CheckPolicy(JsonNode? node, int depth)
        {
            if (depth > 16 || ++scanned > 4096) throw Error("schema_selection_limit");
            if (node is not JsonObject schema) throw Error("schema_selection_invalid");
            if (depth > 0 && (schema.ContainsKey("$id") || schema.ContainsKey("$schema"))) throw Error("schema_reference_scope_unsupported");
            if (schema["$ref"] is { } reference)
            {
                if (reference is not JsonValue value || !value.TryGetValue<string>(out var address)
                    || !address.StartsWith("#/", StringComparison.Ordinal) && address != "#") throw Error("schema_reference_remote_unsupported");
                if (schema.Any(p => p.Key is not ("$ref" or "$schema" or "$id" or "title" or "description" or "$defs" or "definitions")))
                    throw Error("schema_reference_siblings_unsupported");
                if (!references.Add(address)) throw Error("schema_reference_cycle");
                CheckPolicy(At(root, Uri.UnescapeDataString(address[1..]), requireSchemaPath: true), depth + 1);
                references.Remove(address);
            }
            foreach (var (key, child) in schema)
            {
                if (key is "properties" or "$defs" or "definitions" && child is JsonObject map)
                    foreach (var entry in map) CheckPolicy(entry.Value, depth + 1);
                else if (key is "items" || key == "additionalProperties" && child is JsonObject) CheckPolicy(child, depth + 1);
                else if (key is "anyOf" or "oneOf" or "allOf" && child is JsonArray alternatives)
                    foreach (var entry in alternatives) CheckPolicy(entry, depth + 1);
            }
        }

        JsonObject Expand(JsonNode? node, HashSet<string> active, int depth)
        {
            if (depth > 16 || ++count > 2048) throw Error("schema_selection_limit");
            if (node is not JsonObject schema) throw Error("schema_selection_invalid");
            if (depth > 0 && schema.ContainsKey("$id")) throw Error("schema_reference_scope_unsupported");
            if (schema["$ref"] is { } reference)
            {
                if (reference is not JsonValue value || !value.TryGetValue<string>(out var text)
                    || !text.StartsWith("#/", StringComparison.Ordinal) && text != "#")
                    throw Error("schema_reference_remote_unsupported");
                if (schema.Any(p => p.Key is not ("$ref" or "$schema" or "$id" or "title" or "description" or "$defs" or "definitions")))
                    throw Error("schema_reference_siblings_unsupported");
                if (!active.Add(text)) throw Error("schema_reference_cycle");
                var resolved = Expand(At(root, Uri.UnescapeDataString(text[1..]), requireSchemaPath: true), active, depth + 1);
                active.Remove(text);
                foreach (var annotation in schema.Where(p => p.Key is "title" or "description"))
                    resolved[annotation.Key] = annotation.Value?.DeepClone();
                return resolved;
            }
            var result = new JsonObject();
            foreach (var (key, child) in schema)
            {
                if (key is "$defs" or "definitions") continue; // References are expanded against the original document.
                if (key == "properties" && child is JsonObject properties)
                {
                    var projected = new JsonObject();
                    foreach (var property in properties) projected[property.Key] = Expand(property.Value, active, depth + 1);
                    result[key] = projected;
                }
                else if (key is "items" || key == "additionalProperties" && child is JsonObject)
                    result[key] = Expand(child, active, depth + 1);
                else if (key is "anyOf" or "oneOf" or "allOf" && child is JsonArray alternatives)
                {
                    var projected = new JsonArray();
                    foreach (var alternative in alternatives) projected.Add(Expand(alternative, active, depth + 1));
                    result[key] = projected;
                }
                else
                {
                    expandedBytes += Encoding.UTF8.GetByteCount(key) + (child is null ? 4 : Encoding.UTF8.GetByteCount(child.ToJsonString()));
                    if (expandedBytes > MaximumBytes) throw Error("schema_selection_too_large");
                    result[key] = child?.DeepClone();
                }
            }
            return result;
        }
    }

    private static JsonNode At(JsonNode root, string schemaPath, bool requireSchemaPath)
    {
        if (schemaPath.Length > 2048 || schemaPath.Length > 0 && schemaPath[0] != '/') throw Error("schema_pointer_invalid");
        JsonNode? current = root;
        var parts = schemaPath.Length == 0 ? [] : schemaPath[1..].Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            for (var j = 0; j < parts[i].Length; j++)
                if (parts[i][j] == '~' && (++j == parts[i].Length || parts[i][j] is not ('0' or '1')))
                    throw Error("schema_pointer_invalid");
            parts[i] = parts[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (requireSchemaPath)
            {
                if (part is "properties" or "$defs" or "definitions" or "anyOf" or "oneOf" or "allOf")
                {
                    current = current is JsonObject map ? map[part] : null;
                    if (++i >= parts.Length) throw Error("schema_pointer_invalid");
                    part = parts[i];
                }
                else if (part is not ("items" or "additionalProperties")) throw Error("schema_pointer_invalid");
            }
            current = current switch
            {
                JsonObject obj => obj[part],
                JsonArray array when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && (part == "0" || !part.StartsWith('0')) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current is null) throw Error("schema_pointer_not_found");
        }
        return current ?? throw Error("schema_pointer_not_found");
    }

    public static JsonElement ResolveLocalReferences(JsonElement definition)
    {
        return NeedsResolution(definition) ? JsonSchemaSelection.Select(definition) : definition;
        static bool NeedsResolution(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in schema.EnumerateObject())
            {
                if (property.Name is "$ref" or "$defs" or "definitions") return true;
                if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.EnumerateObject().Any(p => NeedsResolution(p.Value))) return true;
                if (property.Name is "items" or "additionalProperties" && NeedsResolution(property.Value)) return true;
                if (property.Name is "anyOf" or "allOf" or "oneOf" && property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.EnumerateArray().Any(NeedsResolution)) return true;
            }
            return false;
        }
    }

    public static string DefinitionHash(string format, string? dialect, JsonElement content, bool published = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (published) { writer.WriteString("dialect", dialect); writer.WriteString("format", format); }
            else { writer.WriteString("format", format); writer.WriteString("dialect", dialect); }
            writer.WritePropertyName("content"); Write(writer, content); writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
            else value.WriteTo(writer);
        }
    }

    private static SchemaSelectionException Error(string code) => new(code);
}
