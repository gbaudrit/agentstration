using System.Text.Json;

namespace Agentstration.Storage.Contracts;

/// <summary>Canonical produced JSON bytes: independent of JSONB key order or numeric lexical normalization.</summary>
public static class StorageDocumentEncoding
{
    public static byte[] Encode(JsonElement document)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(writer, document, 0);
        return bytes.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 16) throw new ArgumentException("JSON nesting exceeds its bound.", nameof(value));
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item, depth + 1); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetDecimal().ToString("G29", System.Globalization.CultureInfo.InvariantCulture)); break;
            default: value.WriteTo(writer); break;
        }
    }
}
