using System.Text.Json;

namespace Agentstration.Web.Components.JsonSchema;

public static class JsonSchemaInputSchema
{
    public static JsonElement? ObjectProperty(JsonElement? schema, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (schema is not { ValueKind: JsonValueKind.Object }
            || !schema.Value.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
            return null;
        return property.Clone();
    }
}
