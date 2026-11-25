using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Schemas.Contracts;

public sealed record JsonCollectionContract(string DataPath, string SchemaPath, SchemaBinding Binding, SchemaBindingSnapshot Item);
public sealed record JsonCollectionCandidates(IReadOnlyList<JsonCollectionContract> Collections, string? Diagnostic = null);

/// <summary>Enumerates declared object collections; never examines an instance or invents array indexing.</summary>
public static class JsonCollectionContracts
{
    public static JsonCollectionCandidates Discover(SchemaBindingSnapshot parent)
    {
        if (parent.Format != SchemaFormatIds.JsonSchema) return new([], "schema_format_incompatible");
        var found = new List<JsonCollectionContract>();
        try
        {
            _ = JsonSchemaSelection.Select(parent.Content); // Existing bounded reference/cycle policy.
            Walk(parent.Content, "", "", 0);
            return new(found);
        }
        catch (SchemaSelectionException error) { return new([], error.Code); }
        catch (DataContractException error) { return new([], error.Code); }

        void Walk(JsonElement node, string dataPath, string schemaPath, int depth)
        {
            if (depth > 12 || found.Count >= 128) throw new SchemaSelectionException("schema_selection_limit");
            if (node.TryGetProperty("$ref", out var reference))
            {
                var path = Uri.UnescapeDataString(reference.GetString()![1..]);
                // Resolve the target but keep its exact location for derived evidence.
                node = At(parent.Content, path);
                schemaPath = path;
            }
            if (node.TryGetProperty("anyOf", out _) || node.TryGetProperty("oneOf", out _) || node.TryGetProperty("allOf", out _))
                throw new SchemaSelectionException("schema_collection_ambiguous");
            if (!node.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() == "array" && node.TryGetProperty("items", out _))
            {
                var itemPath = schemaPath + "/items";
                var item = JsonSchemaSelection.Select(parent.Content, itemPath);
                if (!item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String || itemType.GetString() != "object") return;
                _ = DataContracts.Declared(item, "json.explode/item");
                var absolute = (parent.Source?.SchemaPath ?? "") + itemPath;
                var source = parent.Source is null ? null : parent.Source with { SchemaPath = absolute };
                var hash = JsonSchemaSelection.DefinitionHash(parent.Format, parent.Dialect, item);
                var snapshot = new SchemaBindingSnapshot("$.parameters.itemSchema", parent.Format, parent.Dialect, item, hash,
                    source, parent.SchemaUid, parent.SchemaGeneration)
                { ParentDefinitionHash = parent.ParentDefinitionHash ?? parent.DefinitionHash };
                var binding = source is null ? new SchemaBinding { Inline = new() { Format = parent.Format, Dialect = parent.Dialect, Content = item } }
                    : new SchemaBinding { Reference = source };
                found.Add(new(dataPath, absolute, binding, snapshot));
                return; // Descending through an array would require an unsupported wildcard/index policy.
            }
            if (type.GetString() == "object" && node.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                {
                    var escaped = property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    Walk(property.Value, dataPath + "/" + escaped, schemaPath + "/properties/" + escaped, depth + 1);
                }
        }
    }

    private static JsonElement At(JsonElement root, string path)
    {
        foreach (var part in path.Length == 0 ? [] : path[1..].Split('/'))
            root = root.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        return root;
    }
}
