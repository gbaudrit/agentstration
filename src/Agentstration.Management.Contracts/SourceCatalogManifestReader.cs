using System.Globalization;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed class SourceCatalogManifestReader : ISourceCatalogManifestReader
{
    public const int MaximumManifestBytes = SourceCatalogLimits.MaximumManifestBytes;
    public const int MaximumEntries = 256;
    public const int MaximumVariantsPerEntry = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "definition"
    };

    public ParsedSourceCatalog ReadCatalog(string content, string declaredKind)
    {
        var document = ReadDocument(content, "Source catalog");
        ValidateEnvelope(document, declaredKind, "Source catalog");
        try
        {
            return declaredKind switch
            {
                SourceCatalogKinds.Bootstrap => Bootstrap(ResourceManifestSerializer.FromJsonStrict<BootstrapCatalogManifest>(document.GetRawText())),
                SourceCatalogKinds.Pack => Pack(ResourceManifestSerializer.FromJsonStrict<PackCatalogManifest>(document.GetRawText())),
                _ => throw Invalid("source_catalog_kind_unsupported", $"Catalog kind '{declaredKind}' is not supported.")
            };
        }
        catch (SourceValidationException) { throw; }
        catch (JsonException exception)
        {
            throw Invalid("source_catalog_invalid", $"Source catalog '{declaredKind}' is invalid: {exception.Message}");
        }
    }

    public SourceBootstrapProfileContract ReadBootstrapProfile(string content)
    {
        var document = ReadDocument(content, "Bootstrap Profile descriptor");
        ValidateEnvelope(document, BootstrapResourceKinds.BootstrapProfile, "Bootstrap Profile descriptor");
        try
        {
            var resource = ResourceManifestSerializer.FromJsonStrict<BootstrapResourceDocument>(document.GetRawText());
            var definition = resource.Definition.Deserialize<ProfileDefinition>(SerializerOptions)
                ?? throw Invalid("source_bootstrap_profile_invalid", "Bootstrap Profile descriptor requires a definition.");
            if (!Enum.TryParse<BootstrapProfileScope>(definition.TargetScope, true, out var scope))
                throw Invalid("source_bootstrap_scope_invalid", $"Bootstrap Profile '{resource.Metadata.Name}' uses unsupported targetScope '{definition.TargetScope}'.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new List<SourceBootstrapBindingContract>(definition.Bindings.Count);
            foreach (var binding in definition.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Name) || !string.Equals(binding.Name, binding.Name.Trim(), StringComparison.Ordinal))
                    throw Invalid("source_bootstrap_binding_invalid", $"Bootstrap Profile '{resource.Metadata.Name}' contains an invalid binding name.");
                if (!names.Add(binding.Name))
                    throw Invalid("source_bootstrap_binding_duplicate", $"Bootstrap Profile '{resource.Metadata.Name}' declares binding '{binding.Name}' more than once.");
                if (binding.TargetKind is null)
                    throw Invalid("source_bootstrap_binding_invalid", $"Bootstrap Profile binding '{binding.Name}' requires targetKind.");
                bindings.Add(new(binding.Name, binding.TargetKind.Value, binding.Required));
            }
            return new(resource.Metadata.Name, scope, bindings.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        }
        catch (SourceValidationException) { throw; }
        catch (JsonException exception)
        {
            throw Invalid("source_bootstrap_profile_invalid", $"Bootstrap Profile descriptor is invalid: {exception.Message}");
        }
    }

    private static ParsedSourceCatalog Bootstrap(BootstrapCatalogManifest manifest)
    {
        ValidateCatalogIdentity(manifest.ApiVersion, manifest.Kind, manifest.Metadata.Name, SourceCatalogKinds.Bootstrap);
        if (string.IsNullOrWhiteSpace(manifest.Definition.DisplayName))
            throw Invalid("source_catalog_display_name_missing", "BootstrapCatalog definition.displayName is required.");
        ValidateCount(manifest.Definition.Entries.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Definition.Entries)
        {
            ValidateName(entry.Name, "Bootstrap catalog entry");
            if (!names.Add(entry.Name)) throw Invalid("source_catalog_entry_duplicate", $"Bootstrap catalog entry '{entry.Name}' is declared more than once.");
            if (entry.Variants.Count is 0 or > MaximumVariantsPerEntry)
                throw Invalid("source_catalog_variants_invalid", $"Bootstrap catalog entry '{entry.Name}' must declare 1 to {MaximumVariantsPerEntry} variants.");
            var locales = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variant in entry.Variants)
            {
                ValidateLocale(variant.Locale);
                if (!locales.Add(variant.Locale)) throw Invalid("source_catalog_locale_duplicate", $"Locale '{variant.Locale}' is declared more than once for '{entry.Name}'.");
                if (string.IsNullOrWhiteSpace(variant.Path)) throw Invalid("source_catalog_path_invalid", $"Variant '{entry.Name}/{variant.Locale}' requires a path.");
                if (!paths.Add(variant.Path)) throw Invalid("source_catalog_path_duplicate", $"Variant path '{variant.Path}' is declared more than once for '{entry.Name}'.");
            }
            ValidateLocale(entry.DefaultLocale);
            if (!locales.Contains(entry.DefaultLocale))
                throw Invalid("source_catalog_default_locale_missing", $"Default locale '{entry.DefaultLocale}' has no variant for '{entry.Name}'.");
        }
        return new(manifest.Kind, manifest.Metadata.Name, manifest, null);
    }

    private static ParsedSourceCatalog Pack(PackCatalogManifest manifest)
    {
        ValidateCatalogIdentity(manifest.ApiVersion, manifest.Kind, manifest.Metadata.Name, SourceCatalogKinds.Pack);
        if (string.IsNullOrWhiteSpace(manifest.Definition.DisplayName))
            throw Invalid("source_catalog_display_name_missing", "PackCatalog definition.displayName is required.");
        ValidateCount(manifest.Definition.Entries.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Definition.Entries)
        {
            ValidateName(entry.Name, "Pack catalog entry");
            if (!names.Add(entry.Name)) throw Invalid("source_catalog_entry_duplicate", $"Pack catalog entry '{entry.Name}' is declared more than once.");
            if (string.IsNullOrWhiteSpace(entry.Path)) throw Invalid("source_catalog_path_invalid", $"Pack catalog entry '{entry.Name}' requires a path.");
        }
        return new(manifest.Kind, manifest.Metadata.Name, null, manifest);
    }

    private static JsonElement ReadDocument(string content, string label)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumManifestBytes)
            throw Invalid("source_catalog_size_limit", $"{label} cannot exceed {MaximumManifestBytes} UTF-8 bytes.");
        try
        {
            var documents = ResourceManifestSerializer.FromYamlDocuments<JsonElement>(content);
            if (documents.Count != 1) throw Invalid("source_catalog_document_count", $"{label} must contain exactly one YAML document.");
            if (documents[0].ValueKind != JsonValueKind.Object) throw Invalid("source_catalog_invalid", $"{label} must be a YAML object.");
            return documents[0];
        }
        catch (SourceValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            throw Invalid("source_catalog_invalid", $"{label} contains invalid YAML: {exception.Message}");
        }
    }

    private static void ValidateEnvelope(JsonElement document, string kind, string label)
    {
        foreach (var property in document.EnumerateObject())
            if (!EnvelopeProperties.Contains(property.Name)) throw Invalid("source_catalog_envelope_invalid", $"{label} contains unsupported top-level property '{property.Name}'.");
        if (!document.TryGetProperty("apiVersion", out var apiVersion) || apiVersion.GetString() != ManagementApiVersions.CoreV1)
            throw Invalid("source_catalog_api_version_invalid", $"{label} must use apiVersion '{ManagementApiVersions.CoreV1}'.");
        if (!document.TryGetProperty("kind", out var actualKind) || !string.Equals(actualKind.GetString(), kind, StringComparison.Ordinal))
            throw Invalid("source_catalog_kind_mismatch", $"{label} must use declared kind '{kind}'.");
        if (!document.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
            throw Invalid("source_catalog_definition_missing", $"{label} requires an object definition.");
    }

    private static void ValidateCatalogIdentity(string apiVersion, string kind, string name, string expectedKind)
    {
        if (apiVersion != ManagementApiVersions.CoreV1 || kind != expectedKind) throw Invalid("source_catalog_invalid", $"Catalog must use '{ManagementApiVersions.CoreV1}' and kind '{expectedKind}'.");
        ValidateName(name, "Catalog metadata.name");
    }

    private static void ValidateCount(int count)
    {
        if (count > MaximumEntries) throw Invalid("source_catalog_entry_limit", $"A Source catalog cannot contain more than {MaximumEntries} entries.");
    }

    private static void ValidateName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
            throw Invalid("source_catalog_name_invalid", $"{label} '{value}' is not a portable identifier.");
    }

    private static void ValidateLocale(string value)
    {
        if (string.Equals(value, "neutral", StringComparison.Ordinal)) return;
        string canonical;
        try
        {
            canonical = CultureInfo.GetCultureInfo(value).Name;
        }
        catch (CultureNotFoundException)
        {
            throw Invalid("source_catalog_locale_invalid", $"Locale '{value}' must be a canonical BCP 47 identifier or 'neutral'.");
        }
        if (canonical.Length == 0 || !string.Equals(canonical, value, StringComparison.Ordinal))
            throw Invalid("source_catalog_locale_invalid", $"Locale '{value}' must be a canonical BCP 47 identifier or 'neutral'.");
    }

    private sealed record ProfileDefinition
    {
        public string TargetScope { get; init; } = "workspace";
        public IReadOnlyList<ProfileBindingDefinition> Bindings { get; init; } = [];
    }

    private sealed record ProfileBindingDefinition
    {
        public string Name { get; init; } = string.Empty;
        public BootstrapBindingTargetKind? TargetKind { get; init; }
        public bool Required { get; init; } = true;
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
