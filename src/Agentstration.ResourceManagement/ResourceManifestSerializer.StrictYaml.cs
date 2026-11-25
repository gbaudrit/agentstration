using System.Buffers;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Agentstration.ResourceManagement;

public static partial class ResourceManifestSerializer
{
    private static readonly JsonSerializerOptions StrictYamlOptions;
    /// <summary>One JSON-compatible YAML document. Rejects aliases, tags, duplicate/unknown keys and excess depth.</summary>
    public static T FromYamlStrict<T>(string manifest)
    {
        var parser = new Parser(new StringReader(manifest));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteNode(parser, writer, 0);
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        return JsonSerializer.Deserialize<T>(buffer.WrittenSpan, StrictYamlOptions)
            ?? throw new JsonException("The resource manifest is empty.");
    }

    private static void WriteNode(IParser parser, Utf8JsonWriter writer, int depth)
    {
        if (depth > 32) throw new JsonException("The YAML nesting limit was exceeded.");
        if (parser.Current is not NodeEvent node || !node.Anchor.IsEmpty || !node.Tag.IsEmpty)
            throw new JsonException("Explicit YAML anchors, aliases and tags are not supported.");
        if (parser.TryConsume<MappingStart>(out _))
        {
            writer.WriteStartObject();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!parser.Accept<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                if (!key.Anchor.IsEmpty || !key.Tag.IsEmpty || key.Value == "<<" || !keys.Add(key.Value))
                    throw new JsonException("YAML mapping keys must be unique, untagged strings.");
                writer.WritePropertyName(key.Value);
                WriteNode(parser, writer, depth + 1);
            }
            parser.Consume<MappingEnd>();
            writer.WriteEndObject();
        }
        else if (parser.TryConsume<SequenceStart>(out _))
        {
            writer.WriteStartArray();
            while (!parser.Accept<SequenceEnd>(out _)) WriteNode(parser, writer, depth + 1);
            parser.Consume<SequenceEnd>();
            writer.WriteEndArray();
        }
        else
        {
            var scalar = parser.Consume<Scalar>();
            if (scalar.Style == ScalarStyle.Plain)
            {
                if (scalar.Value is "null" or "~" or "") { writer.WriteNullValue(); return; }
                if (scalar.Value is "true" or "false") { writer.WriteBooleanValue(scalar.Value == "true"); return; }
                if (scalar.Value[0] is '-' or >= '0' and <= '9')
                {
                    try
                    {
                        using var number = JsonDocument.Parse(scalar.Value);
                        if (number.RootElement.ValueKind == JsonValueKind.Number) { number.RootElement.WriteTo(writer); return; }
                    }
                    catch (JsonException) { /* Values such as UUIDs and 01 remain strings. */ }
                }
            }
            writer.WriteStringValue(scalar.Value);
        }
    }
}
