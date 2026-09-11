using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Agentstration.Management.Contracts;

public sealed partial class SourceRegistryIndexReader : ISourceRegistryIndexReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly IDeserializer StrictYamlDeserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public ParsedSourceRegistryIndex Read(string rawDocument, string fileName, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(rawDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(baseUri);
        SourceRegistryReferenceResolver.ValidateBaseUri(baseUri);
        if (Encoding.UTF8.GetByteCount(rawDocument) > SourceRegistryLimits.MaximumIndexDocumentBytes)
            throw Invalid("source_registry_index_size_limit", $"Source Registry Index documents cannot exceed {SourceRegistryLimits.MaximumIndexDocumentBytes} UTF-8 bytes.");

        try
        {
            var document = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".json" => ParseJson(rawDocument),
                ".yaml" or ".yml" => ParseYaml(rawDocument),
                _ => throw Invalid("source_registry_index_file_name_invalid", "Registry Index files must use a .json, .yaml, or .yml suffix.")
            };
            RejectNulls(document, "$");
            var manifest = JsonSerializer.Deserialize<SourceRegistryIndexManifest>(document.GetRawText(), StrictJsonOptions)
                ?? throw Invalid("source_registry_index_invalid", "The Source Registry Index document is empty.");
            var normalized = ValidateAndNormalize(manifest, baseUri);
            var canonical = Canonicalize(normalized);
            return new(normalized, canonical, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonical))}");
        }
        catch (SourceValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or YamlException or InvalidOperationException or DecoderFallbackException)
        {
            throw Invalid("source_registry_index_invalid", $"The Source Registry Index document is invalid: {exception.Message}");
        }
    }

    private static JsonElement ParseJson(string rawDocument)
    {
        using var document = JsonDocument.Parse(rawDocument, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = SourceRegistryLimits.MaximumDepth
        });
        RejectDuplicateProperties(document.RootElement, "$");
        return document.RootElement.Clone();
    }

    private static JsonElement ParseYaml(string rawDocument)
    {
        ValidateYamlEvents(rawDocument);
        var yaml = StrictYamlDeserializer.Deserialize<object?>(rawDocument);
        return JsonSerializer.SerializeToElement(ToJsonValue(yaml, "$"));
    }

    private static void ValidateYamlEvents(string rawDocument)
    {
        var parser = new Parser(new StringReader(rawDocument));
        var documents = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    documents++;
                    break;
                case MappingStart or SequenceStart:
                    if (++depth > SourceRegistryLimits.MaximumDepth)
                        throw Invalid("source_registry_index_depth_limit", $"Source Registry Index nesting cannot exceed {SourceRegistryLimits.MaximumDepth} levels.");
                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
                case AnchorAlias:
                    throw Invalid("source_registry_index_yaml_feature_invalid", "YAML aliases are not permitted in Source Registry Index documents.");
            }
            if (parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Invalid("source_registry_index_yaml_feature_invalid", "YAML anchors and explicit tags are not permitted in Source Registry Index documents.");
        }
        if (documents != 1)
            throw Invalid("source_registry_index_document_count", "A Source Registry Index input must contain exactly one YAML document.");
    }

    private static object? ToJsonValue(object? value, string path) => value switch
    {
        null => null,
        string or bool or long or ulong or int or uint or short or ushort or byte or sbyte or double or float or decimal => value,
        IDictionary<object, object> mapping => ConvertMapping(mapping, path),
        IDictionary<string, object> mapping => ConvertMapping(mapping.Select(pair => new KeyValuePair<object, object>(pair.Key, pair.Value)), path),
        IEnumerable<object> sequence => sequence.Select((item, index) => ToJsonValue(item, $"{path}[{index}]")).ToArray(),
        _ => throw Invalid("source_registry_index_yaml_value_invalid", $"YAML value at '{path}' is not JSON-compatible.")
    };

    private static Dictionary<string, object?> ConvertMapping(IEnumerable<KeyValuePair<object, object>> mapping, string path)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in mapping)
        {
            if (pair.Key is not string key)
                throw Invalid("source_registry_index_yaml_key_invalid", $"YAML mapping keys at '{path}' must be strings.");
            if (key == "<<")
                throw Invalid("source_registry_index_yaml_feature_invalid", "YAML merge keys are not permitted in Source Registry Index documents.");
            if (!result.TryAdd(key, ToJsonValue(pair.Value, $"{path}.{key}")))
                throw Invalid("source_registry_index_property_duplicate", $"Property '{key}' is declared more than once at '{path}'.");
        }
        return result;
    }

    private static void RejectDuplicateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid("source_registry_index_property_duplicate", $"Property '{property.Name}' is declared more than once at '{path}'.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item, $"{path}[{index++}]");
        }
    }

    private static void RejectNulls(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            throw Invalid("source_registry_index_null_invalid", $"Explicit null is not permitted at '{path}'. Omit optional properties instead.");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) RejectNulls(property.Value, $"{path}.{property.Name}");
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectNulls(item, $"{path}[{index++}]");
        }
    }

    private static SourceRegistryIndexManifest ValidateAndNormalize(SourceRegistryIndexManifest manifest, Uri baseUri)
    {
        if (manifest.ApiVersion != ManagementApiVersions.CoreV1)
            throw Invalid("source_registry_index_api_version_unsupported", $"Supported Source Registry Index apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (manifest.Kind != SourceRegistryKinds.SourceRegistryIndex)
            throw Invalid("source_registry_index_kind_invalid", $"Source Registry Index kind must be '{SourceRegistryKinds.SourceRegistryIndex}'.");
        if (manifest.Metadata is null || manifest.Definition is null)
            throw Invalid("source_registry_index_envelope_invalid", "metadata and definition are required.");
        ValidateName(manifest.Metadata.Name, "metadata.name");
        ValidateOptionalText(manifest.Metadata.DisplayName, 256, "metadata.displayName");
        var catalogs = manifest.Definition.Catalogs
            ?? throw Invalid("source_registry_index_catalogs_invalid", "definition.catalogs is required.");
        if (catalogs.Count is 0 or > SourceRegistryLimits.MaximumCatalogs)
            throw Invalid("source_registry_index_catalog_limit", $"A Source Registry Index requires 1 to {SourceRegistryLimits.MaximumCatalogs} catalogs.");
        EnsureUnique(catalogs.Select(value => value.Name));
        foreach (var catalog in catalogs)
        {
            ValidateName(catalog.Name, "definition.catalogs[].name");
            var bounds = catalog.Compatibility?.Agentstration
                ?? throw Invalid("source_registry_index_compatibility_missing", $"Catalog '{catalog.Name}' requires compatibility.agentstration.");
            if (!SourceSemanticVersion.TryParse(bounds.MinVersion, out var minimum))
                throw Invalid("source_registry_index_version_invalid", $"Catalog '{catalog.Name}' minVersion must be a valid Semantic Version.");
            if (bounds.MaxVersionExclusive is { } maximumValue)
            {
                if (!SourceSemanticVersion.TryParse(maximumValue, out var maximum))
                    throw Invalid("source_registry_index_version_invalid", $"Catalog '{catalog.Name}' maxVersionExclusive must be a valid Semantic Version.");
                if (minimum.CompareTo(maximum) >= 0)
                    throw Invalid("source_registry_index_interval_invalid", $"Catalog '{catalog.Name}' maxVersionExclusive must be greater than minVersion.");
            }
            ValidateText(catalog.RegistryUrl, 2048, $"Catalog '{catalog.Name}' registryUrl");
            _ = SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, catalog.RegistryUrl);
            if (!DigestPattern().IsMatch(catalog.RegistryDigest))
                throw Invalid("source_registry_index_digest_invalid", $"Catalog '{catalog.Name}' registryDigest must be lowercase sha256 hexadecimal.");
        }
        return manifest with
        {
            Definition = manifest.Definition with
            {
                Catalogs = catalogs.OrderBy(value => value.Name, Utf8OrdinalComparer.Instance).ToArray()
            }
        };
    }

    private static byte[] Canonicalize(SourceRegistryIndexManifest manifest)
    {
        var json = new StringBuilder();
        json.Append("{\"apiVersion\":");
        WriteString(json, manifest.ApiVersion);
        json.Append(",\"definition\":{\"catalogs\":[");
        for (var index = 0; index < manifest.Definition.Catalogs.Count; index++)
        {
            if (index > 0) json.Append(',');
            var catalog = manifest.Definition.Catalogs[index];
            var bounds = catalog.Compatibility.Agentstration!;
            json.Append("{\"compatibility\":{\"agentstration\":{");
            if (bounds.MaxVersionExclusive is { } maximum)
            {
                json.Append("\"maxVersionExclusive\":");
                WriteString(json, maximum);
                json.Append(',');
            }
            json.Append("\"minVersion\":");
            WriteString(json, bounds.MinVersion!);
            json.Append("}},\"name\":");
            WriteString(json, catalog.Name);
            json.Append(",\"registryDigest\":");
            WriteString(json, catalog.RegistryDigest);
            json.Append(",\"registryUrl\":");
            WriteString(json, catalog.RegistryUrl);
            json.Append('}');
        }
        json.Append("]},\"kind\":");
        WriteString(json, manifest.Kind);
        json.Append(",\"metadata\":{");
        if (manifest.Metadata.DisplayName is { } displayName)
        {
            json.Append("\"displayName\":");
            WriteString(json, displayName);
            json.Append(',');
        }
        json.Append("\"name\":");
        WriteString(json, manifest.Metadata.Name);
        json.Append("}}");
        return StrictUtf8.GetBytes(json.ToString());
    }

    private static void WriteString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\t': json.Append("\\t"); break;
                case '\n': json.Append("\\n"); break;
                case '\f': json.Append("\\f"); break;
                case '\r': json.Append("\\r"); break;
                case <= 0x1f: json.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture)); break;
                default: json.Append(rune); break;
            }
        }
        json.Append('"');
    }

    private static void ValidateName(string value, string field)
    {
        ValidateText(value, 63, field);
        if (!NamePattern().IsMatch(value))
            throw Invalid("source_registry_index_identity_invalid", $"{field} is not a portable registry name.");
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string field)
    {
        if (value is not null) ValidateText(value, maximumLength, field);
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (value is null || !value.IsNormalized(NormalizationForm.FormC))
            throw Invalid("source_registry_index_string_invalid", $"{field} must be valid NFC-normalized Unicode.");
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw Invalid("source_registry_index_string_invalid", $"{field} must be valid NFC-normalized Unicode.");
        }
        var scalarCount = value.EnumerateRunes().Count();
        if (scalarCount is 0 || scalarCount > maximumLength)
            throw Invalid("source_registry_index_string_length", $"{field} must contain 1 to {maximumLength} Unicode scalar values.");
    }

    private static void EnsureUnique(IEnumerable<string> names)
    {
        var duplicate = names.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw Invalid("source_registry_index_catalog_duplicate", $"Catalog '{duplicate.Key}' is declared more than once.");
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    private sealed class Utf8OrdinalComparer : IComparer<string>
    {
        public static Utf8OrdinalComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }
}
