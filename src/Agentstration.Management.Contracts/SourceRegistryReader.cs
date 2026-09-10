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

public sealed partial class SourceRegistryReader
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

    public ParsedSourceRegistry Read(string rawDocument, string fileName)
    {
        ArgumentNullException.ThrowIfNull(rawDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Encoding.UTF8.GetByteCount(rawDocument) > SourceRegistryLimits.MaximumDocumentBytes)
            throw Invalid("source_registry_size_limit", $"Source Registry documents cannot exceed {SourceRegistryLimits.MaximumDocumentBytes} UTF-8 bytes.");

        try
        {
            var extension = Path.GetExtension(fileName);
            var document = extension.ToLowerInvariant() switch
            {
                ".json" => ParseJson(rawDocument),
                ".yaml" or ".yml" => ParseYaml(rawDocument),
                _ => throw Invalid("source_registry_file_name_invalid", "Registry files must use a .json, .yaml, or .yml suffix.")
            };
            RejectNulls(document, "$");
            var manifest = JsonSerializer.Deserialize<SourceRegistryManifest>(document.GetRawText(), StrictJsonOptions)
                ?? throw Invalid("source_registry_invalid", "The Source Registry document is empty.");
            var normalized = ValidateAndNormalize(manifest);
            var canonical = Canonicalize(normalized);
            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonical))}";
            return new(normalized, canonical, digest);
        }
        catch (SourceValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or YamlException or InvalidOperationException or DecoderFallbackException)
        {
            throw Invalid("source_registry_invalid", $"The Source Registry document is invalid: {exception.Message}");
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
                    depth++;
                    if (depth > SourceRegistryLimits.MaximumDepth)
                        throw Invalid("source_registry_depth_limit", $"Source Registry nesting cannot exceed {SourceRegistryLimits.MaximumDepth} levels.");
                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
                case AnchorAlias:
                    throw Invalid("source_registry_yaml_feature_invalid", "YAML aliases are not permitted in Source Registry documents.");
            }

            if (parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Invalid("source_registry_yaml_feature_invalid", "YAML anchors and explicit tags are not permitted in Source Registry documents.");
        }

        if (documents != 1)
            throw Invalid("source_registry_document_count", "A Source Registry input must contain exactly one YAML document.");
    }

    private static object? ToJsonValue(object? value, string path) => value switch
    {
        null => null,
        string or bool or long or ulong or int or uint or short or ushort or byte or sbyte or double or float or decimal => value,
        IDictionary<object, object> mapping => ConvertMapping(mapping, path),
        IDictionary<string, object> mapping => ConvertMapping(mapping.Select(pair => new KeyValuePair<object, object>(pair.Key, pair.Value)), path),
        IEnumerable<object> sequence => sequence.Select((item, index) => ToJsonValue(item, $"{path}[{index}]")).ToArray(),
        _ => throw Invalid("source_registry_yaml_value_invalid", $"YAML value at '{path}' is not part of the JSON-compatible YAML 1.2 Core schema.")
    };

    private static Dictionary<string, object?> ConvertMapping(IEnumerable<KeyValuePair<object, object>> mapping, string path)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in mapping)
        {
            if (pair.Key is not string key)
                throw Invalid("source_registry_yaml_key_invalid", $"YAML mapping keys at '{path}' must be strings.");
            if (key == "<<")
                throw Invalid("source_registry_yaml_feature_invalid", "YAML merge keys are not permitted in Source Registry documents.");
            if (!result.TryAdd(key, ToJsonValue(pair.Value, $"{path}.{key}")))
                throw Invalid("source_registry_property_duplicate", $"Property '{key}' is declared more than once at '{path}'.");
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
                    throw Invalid("source_registry_property_duplicate", $"Property '{property.Name}' is declared more than once at '{path}'.");
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
            throw Invalid("source_registry_null_invalid", $"Explicit null is not permitted at '{path}'. Omit optional properties instead.");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) RejectNulls(property.Value, $"{path}.{property.Name}");
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectNulls(item, $"{path}[{index++}]");
        }
    }

    private static SourceRegistryManifest ValidateAndNormalize(SourceRegistryManifest manifest)
    {
        if (manifest.ApiVersion != ManagementApiVersions.CoreV1)
            throw Invalid("source_registry_api_version_unsupported", $"Supported Source Registry apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (manifest.Kind != SourceRegistryKinds.SourceRegistry)
            throw Invalid("source_registry_kind_invalid", $"Source Registry kind must be '{SourceRegistryKinds.SourceRegistry}'.");
        if (manifest.Metadata is null || manifest.Definition is null)
            throw Invalid("source_registry_envelope_invalid", "metadata and definition are required.");
        ValidateName(manifest.Metadata.Name, "metadata.name");
        ValidateOptionalText(manifest.Metadata.DisplayName, 256, "metadata.displayName");

        var publishers = manifest.Definition.Publishers
            ?? throw Invalid("source_registry_publishers_invalid", "definition.publishers is required.");
        var sources = manifest.Definition.Sources
            ?? throw Invalid("source_registry_sources_invalid", "definition.sources is required.");
        if (publishers.Count is 0 or > SourceRegistryLimits.MaximumPublishers)
            throw Invalid("source_registry_publisher_limit", $"A Source Registry requires 1 to {SourceRegistryLimits.MaximumPublishers} publishers.");
        if (sources.Count > SourceRegistryLimits.MaximumSources)
            throw Invalid("source_registry_source_limit", $"A Source Registry cannot exceed {SourceRegistryLimits.MaximumSources} Sources.");

        EnsureUnique(publishers.Select(value => value.Name), "source_registry_publisher_duplicate", "publisher");
        foreach (var publisher in publishers)
        {
            ValidateName(publisher.Name, "definition.publishers[].name");
            ValidateOptionalText(publisher.DisplayName, 256, $"Publisher '{publisher.Name}' displayName");
            if (publisher.Status is not (SourceRegistryPublisherStatuses.Declared or SourceRegistryPublisherStatuses.Verified
                or SourceRegistryPublisherStatuses.Official or SourceRegistryPublisherStatuses.Revoked))
                throw Invalid("source_registry_publisher_status_invalid", $"Publisher '{publisher.Name}' has unsupported status '{publisher.Status}'.");
            if (publisher.Url is { } url)
            {
                ValidateText(url, 2048, $"Publisher '{publisher.Name}' url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                    throw Invalid("source_registry_publisher_url_invalid", $"Publisher '{publisher.Name}' url must be an absolute HTTP(S) URL without credentials.");
            }
        }

        EnsureUnique(sources.Select(value => $"{value.Publisher}\0{value.Name}"), "source_registry_source_duplicate", "Source");
        var totalVersions = 0;
        foreach (var source in sources)
        {
            ValidateName(source.Publisher, "definition.sources[].publisher");
            ValidateName(source.Name, "definition.sources[].name");
            if (!publishers.Any(value => value.Name == source.Publisher))
                throw Invalid("source_registry_publisher_reference_invalid", $"Source '{source.Publisher}/{source.Name}' references an undeclared publisher.");
            ValidateOptionalText(source.DisplayName, 256, $"Source '{source.Publisher}/{source.Name}' displayName");
            ValidateOptionalText(source.Description, 4096, $"Source '{source.Publisher}/{source.Name}' description");
            var versions = source.Versions ?? throw Invalid("source_registry_versions_invalid", $"Source '{source.Publisher}/{source.Name}' requires versions.");
            if (versions.Count is 0 or > SourceRegistryLimits.MaximumVersionsPerSource)
                throw Invalid("source_registry_version_limit", $"Source '{source.Publisher}/{source.Name}' requires 1 to {SourceRegistryLimits.MaximumVersionsPerSource} versions.");
            totalVersions += versions.Count;
            EnsureUnique(versions.Select(value => value.Version), "source_registry_version_duplicate", "version");
            foreach (var version in versions)
            {
                ValidateVersion(version.Version, $"Source '{source.Publisher}/{source.Name}' version");
                ValidateText(version.ManifestUrl, 2048, $"Source '{source.Publisher}/{source.Name}@{version.Version}' manifestUrl");
                if (!DigestPattern().IsMatch(version.ManifestDigest))
                    throw Invalid("source_registry_manifest_digest_invalid", $"Source '{source.Publisher}/{source.Name}@{version.Version}' manifestDigest must be lowercase sha256 hexadecimal.");
            }
            if (source.Latest is { } latest)
            {
                ValidateVersion(latest, $"Source '{source.Publisher}/{source.Name}' latest");
                if (versions.Count(value => value.Version == latest) != 1)
                    throw Invalid("source_registry_latest_invalid", $"Source '{source.Publisher}/{source.Name}' latest must identify exactly one declared version.");
            }
        }
        if (sources.GroupBy(value => value.Publisher, StringComparer.Ordinal).Any(group => group.Count() > SourceRegistryLimits.MaximumSourcesPerPublisher))
            throw Invalid("source_registry_source_limit", $"A publisher cannot exceed {SourceRegistryLimits.MaximumSourcesPerPublisher} Sources.");
        if (totalVersions > SourceRegistryLimits.MaximumVersions)
            throw Invalid("source_registry_version_limit", $"A Source Registry cannot exceed {SourceRegistryLimits.MaximumVersions} versions.");

        return manifest with
        {
            Definition = manifest.Definition with
            {
                Publishers = publishers.OrderBy(value => value.Name, Utf8OrdinalComparer.Instance).ToArray(),
                Sources = sources.OrderBy(value => value.Publisher, Utf8OrdinalComparer.Instance)
                    .ThenBy(value => value.Name, Utf8OrdinalComparer.Instance)
                    .Select(value => value with
                    {
                        Versions = value.Versions.OrderBy(version => version.Version, Utf8OrdinalComparer.Instance).ToArray()
                    }).ToArray()
            }
        };
    }

    private static byte[] Canonicalize(SourceRegistryManifest manifest)
    {
        var json = new StringBuilder();
        json.Append("{\"apiVersion\":");
        WriteString(json, manifest.ApiVersion);
        json.Append(",\"definition\":{\"publishers\":[");
        for (var publisherIndex = 0; publisherIndex < manifest.Definition.Publishers.Count; publisherIndex++)
        {
            if (publisherIndex > 0) json.Append(',');
            var publisher = manifest.Definition.Publishers[publisherIndex];
            json.Append('{');
            if (publisher.DisplayName is { } publisherDisplayName)
            {
                json.Append("\"displayName\":");
                WriteString(json, publisherDisplayName);
                json.Append(',');
            }
            json.Append("\"name\":");
            WriteString(json, publisher.Name);
            json.Append(",\"status\":");
            WriteString(json, publisher.Status);
            if (publisher.Url is { } url)
            {
                json.Append(",\"url\":");
                WriteString(json, url);
            }
            json.Append('}');
        }
        json.Append("],\"sources\":[");
        for (var sourceIndex = 0; sourceIndex < manifest.Definition.Sources.Count; sourceIndex++)
        {
            if (sourceIndex > 0) json.Append(',');
            var source = manifest.Definition.Sources[sourceIndex];
            json.Append('{');
            var hasProperty = false;
            WriteOptionalProperty(json, "description", source.Description, ref hasProperty);
            WriteOptionalProperty(json, "displayName", source.DisplayName, ref hasProperty);
            WriteOptionalProperty(json, "latest", source.Latest, ref hasProperty);
            WriteProperty(json, "name", source.Name, ref hasProperty);
            WriteProperty(json, "publisher", source.Publisher, ref hasProperty);
            if (hasProperty) json.Append(',');
            json.Append("\"versions\":[");
            for (var versionIndex = 0; versionIndex < source.Versions.Count; versionIndex++)
            {
                if (versionIndex > 0) json.Append(',');
                var version = source.Versions[versionIndex];
                json.Append("{\"manifestDigest\":");
                WriteString(json, version.ManifestDigest);
                json.Append(",\"manifestUrl\":");
                WriteString(json, version.ManifestUrl);
                json.Append(",\"version\":");
                WriteString(json, version.Version);
                json.Append('}');
            }
            json.Append("]}");
        }
        json.Append("]},\"kind\":");
        WriteString(json, manifest.Kind);
        json.Append(",\"metadata\":{");
        var hasMetadataProperty = false;
        WriteOptionalProperty(json, "displayName", manifest.Metadata.DisplayName, ref hasMetadataProperty);
        WriteProperty(json, "name", manifest.Metadata.Name, ref hasMetadataProperty);
        json.Append("}}");
        return StrictUtf8.GetBytes(json.ToString());
    }

    private static void WriteOptionalProperty(StringBuilder json, string name, string? value, ref bool hasProperty)
    {
        if (value is not null) WriteProperty(json, name, value, ref hasProperty);
    }

    private static void WriteProperty(StringBuilder json, string name, string value, ref bool hasProperty)
    {
        if (hasProperty) json.Append(',');
        WriteString(json, name);
        json.Append(':');
        WriteString(json, value);
        hasProperty = true;
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
                case <= 0x1f:
                    json.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: json.Append(rune); break;
            }
        }
        json.Append('"');
    }

    private static void ValidateName(string value, string field)
    {
        ValidateText(value, 63, field);
        if (!NamePattern().IsMatch(value)) throw Invalid("source_registry_identity_invalid", $"{field} is not a portable registry name.");
    }

    private static void ValidateVersion(string value, string field)
    {
        ValidateText(value, 128, field);
        if (value != value.Trim() || value.EnumerateRunes().Any(Rune.IsControl))
            throw Invalid("source_registry_version_invalid", $"{field} must be trimmed and contain no control character.");
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string field)
    {
        if (value is not null) ValidateText(value, maximumLength, field);
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (value is null || !value.IsNormalized(NormalizationForm.FormC))
            throw Invalid("source_registry_string_invalid", $"{field} must be valid NFC-normalized Unicode.");
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("source_registry_string_invalid", $"{field} must be valid NFC-normalized Unicode.");
        }
        var length = value.EnumerateRunes().Count();
        if (length is 0 || length > maximumLength)
            throw Invalid("source_registry_string_length", $"{field} must contain 1 to {maximumLength} Unicode scalar values.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string code, string label)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw Invalid(code, $"{label} '{duplicate.Key.Replace("\0", "/", StringComparison.Ordinal)}' is declared more than once.");
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
