using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace Agentstration.Asp.Abstractions;

/// <summary>Bounded local schema resolution for validation and typed query-model discovery. Never performs I/O.</summary>
public static class DocumentSchema
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$defs", "$ref", "anyOf", "title", "description", "type", "properties", "required",
        "additionalProperties", "items", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "enum", "format"
    };

    public static JsonElement Normalize(JsonElement schema)
    {
        AspValidation.Require(schema.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(schema.GetRawText()) <= 65536,
            "invalid_schema", "The schema must be a bounded JSON object.");
        AspDocumentValidation.ValidateJson(schema);
        var budget = 2048;
        var result = Expand(schema, schema, "", 0, new(StringComparer.Ordinal), ref budget);
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonObject Expand(JsonElement root, JsonElement schema, string path, int depth,
        HashSet<string> references, ref int budget)
    {
        Require(depth <= 8 && --budget >= 0 && schema.ValueKind == JsonValueKind.Object,
            "limit_exceeded", "The schema exceeds its expansion or depth limit.", path, "$ref");
        foreach (var property in schema.EnumerateObject())
            Require(Keywords.Contains(property.Name), "unsupported_schema", "Unsupported document schema keyword.", Child(path, property.Name), property.Name);
        if (schema.TryGetProperty("$defs", out var definitions))
        {
            Require(definitions.ValueKind == JsonValueKind.Object && definitions.EnumerateObject().Count() <= 64,
                "invalid_schema", "Local definitions must be bounded.", Child(path, "$defs"), "$defs");
            foreach (var definition in definitions.EnumerateObject())
                _ = Expand(root, definition.Value, Child(Child(path, "$defs"), definition.Name), depth + 1, references, ref budget);
        }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Require(reference.ValueKind == JsonValueKind.String, "invalid_schema", "A reference must be a string.", Child(path, "$ref"), "$ref");
            var pointer = reference.GetString()!;
            Require(pointer.StartsWith("#/", StringComparison.Ordinal) && pointer.Length <= 512,
                "unsupported_schema", "Only local JSON Pointer references are supported.", Child(path, "$ref"), "$ref");
            Require(references.Add(pointer), "unsupported_schema", "Recursive document schemas are unsupported.", Child(path, "$ref"), "$ref");
            Require(schema.EnumerateObject().All(p => p.Name is "$ref" or "$defs" or "$schema" or "title" or "description"),
                "unsupported_schema", "Reference siblings are limited to annotations.", path, "$ref");
            var resolved = root;
            foreach (var segment in pointer[2..].Split('/'))
                Require(resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), out resolved),
                    "invalid_schema", "The local schema reference does not exist.", Child(path, "$ref"), "$ref");
            var result = Expand(root, resolved, pointer[1..], depth + 1, references, ref budget);
            references.Remove(pointer);
            foreach (var annotation in schema.EnumerateObject().Where(p => p.Name is "title" or "description"))
                result[annotation.Name] = JsonNode.Parse(annotation.Value.GetRawText());
            return result;
        }
        if (schema.TryGetProperty("anyOf", out var union))
        {
            Require(union.ValueKind == JsonValueKind.Array && union.GetArrayLength() == 2,
                "unsupported_schema", "Only a typed schema plus null is supported for document anyOf.", Child(path, "anyOf"), "anyOf");
            var branches = new List<JsonObject>();
            var index = 0;
            foreach (var branch in union.EnumerateArray()) branches.Add(Expand(root, branch, Child(Child(path, "anyOf"), (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)), depth + 1, references, ref budget));
            var nulls = branches.Where(b => b["type"] is JsonValue t && t.TryGetValue<string>(out var kind) && kind == "null"
                && b.All(p => p.Key is "type" or "title" or "description")).ToArray();
            Require(nulls.Length == 1 && schema.EnumerateObject().All(p => p.Name is "anyOf" or "$defs" or "$schema" or "title" or "description"),
                "unsupported_schema", "Only a typed schema plus null is supported for document anyOf.", Child(path, "anyOf"), "anyOf");
            var result = branches.Single(b => !ReferenceEquals(b, nulls[0]));
            var declared = result["type"];
            Require(declared is JsonValue, "unsupported_schema", "A nullable branch requires one explicit type.", Child(path, "anyOf"), "anyOf");
            result["type"] = new JsonArray(declared!.DeepClone(), JsonValue.Create("null"));
            foreach (var annotation in schema.EnumerateObject().Where(p => p.Name is "$schema" or "title" or "description"))
                result[annotation.Name] = JsonNode.Parse(annotation.Value.GetRawText());
            return result;
        }
        var output = new JsonObject();
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "$defs") continue;
            if (property.Name == "properties")
            {
                Require(property.Value.ValueKind == JsonValueKind.Object, "invalid_schema", "Schema properties must be an object.", Child(path, "properties"), "properties");
                var properties = new JsonObject();
                foreach (var item in property.Value.EnumerateObject()) properties.Add(item.Name, Expand(root, item.Value, Child(Child(path, "properties"), item.Name), depth + 1, references, ref budget));
                output.Add(property.Name, properties);
            }
            else if (property.Name == "items") output.Add(property.Name, Expand(root, property.Value, Child(path, "items"), depth + 1, references, ref budget));
            else
            {
                if (property.Name == "format") Require(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "date-time" or "uri",
                    "unsupported_schema", "Supported asserted formats are date-time and uri.", Child(path, "format"), "format");
                output.Add(property.Name, JsonNode.Parse(property.Value.GetRawText()));
            }
        }
        return output;
    }

    private static string Child(string path, string name) => path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static void Require(bool condition, string code, string message, string path, string keyword)
    {
        if (!condition) throw new AspException(code, message) { SchemaPath = path, SchemaKeyword = keyword };
    }
}
