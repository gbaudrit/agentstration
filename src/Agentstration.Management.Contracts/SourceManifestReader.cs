using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed class SourceManifestReader : ISourceManifestReader
{
    public const int MaximumManifestBytes = 1024 * 1024;
    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "definition"
    };

    public ParsedSourceManifest Read(string rawManifest)
    {
        ArgumentNullException.ThrowIfNull(rawManifest);
        if (Encoding.UTF8.GetByteCount(rawManifest) > MaximumManifestBytes)
            throw new SourceValidationException("source_manifest_size_limit", $"Source Version manifests cannot exceed {MaximumManifestBytes} UTF-8 bytes.");

        try
        {
            var documents = ResourceManifestSerializer.FromYamlDocuments<JsonElement>(rawManifest);
            if (documents.Count != 1)
                throw new SourceValidationException("source_manifest_document_count", "A Source Version import must contain exactly one YAML document.");
            var document = documents[0];
            if (document.ValueKind != JsonValueKind.Object)
                throw new SourceValidationException("source_manifest_invalid", "A Source Version manifest must be a YAML object.");
            foreach (var property in document.EnumerateObject())
            {
                if (!EnvelopeProperties.Contains(property.Name))
                    throw new SourceValidationException("source_manifest_envelope_invalid", $"Top-level property '{property.Name}' is not part of the Agentstration resource envelope; functional fields belong under definition.");
            }
            if (!document.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
                throw new SourceValidationException("source_definition_missing", "A Source Version manifest requires an object definition.");

            var manifest = ResourceManifestSerializer.FromJsonStrict<PublishedSourceVersionManifest>(document.GetRawText());
            var canonical = Canonicalize(document);
            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonical))}";
            return new ParsedSourceManifest(manifest, rawManifest, digest);
        }
        catch (SourceValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            throw new SourceValidationException("source_manifest_invalid", $"The Source Version manifest is invalid: {exception.Message}");
        }
    }

    private static byte[] Canonicalize(JsonElement document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
