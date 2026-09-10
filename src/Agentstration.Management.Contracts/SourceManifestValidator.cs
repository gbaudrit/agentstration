using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed partial class SourceManifestValidator
{
    public void Validate(PublishedSourceVersionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.ApiVersion, ManagementApiVersions.CoreV1, StringComparison.Ordinal))
            throw Invalid("source_api_version_unsupported", $"Supported Source Version apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (!string.Equals(manifest.Kind, SourceKinds.PublishedSourceVersion, StringComparison.Ordinal))
            throw Invalid("source_kind_invalid", $"Source Version manifest kind must be '{SourceKinds.PublishedSourceVersion}'.");
        if (manifest.Metadata is null) throw Invalid("source_metadata_missing", "Source Version metadata is required.");
        if (!manifest.Metadata.Namespace.IsDefault)
            throw Invalid("source_namespace_invalid", "Published Source Version metadata must not select a local resource namespace.");
        ValidatePortableName(manifest.Metadata.Name, "metadata.name");
        if (manifest.Definition is null) throw Invalid("source_definition_missing", "A Source Version definition is required.");
        if (string.IsNullOrWhiteSpace(manifest.Definition.Version)
            || manifest.Definition.Version.Length > 128
            || !string.Equals(manifest.Definition.Version, manifest.Definition.Version.Trim(), StringComparison.Ordinal)
            || manifest.Definition.Version.Any(char.IsControl))
            throw Invalid("source_version_invalid", "definition.version must contain 1 to 128 characters.");
        if (manifest.Definition.Publisher is null) throw Invalid("source_publisher_missing", "definition.publisher is required.");
        ValidatePortableName(manifest.Definition.Publisher.Name, "definition.publisher.name");
        if (manifest.Definition.DisplayName?.Length > 200) throw Invalid("source_display_name_invalid", "definition.displayName cannot exceed 200 characters.");
        if (manifest.Definition.Publisher.Url is { } publisherUrl
            && (!Uri.TryCreate(publisherUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw Invalid("source_publisher_url_invalid", "definition.publisher.url must be an absolute HTTP(S) URL.");

        var bindings = manifest.Definition.Bindings
            ?? throw Invalid("source_bindings_invalid", "definition.bindings must be an array.");
        var channels = manifest.Definition.Channels
            ?? throw Invalid("source_channels_invalid", "definition.channels must be an array.");
        var catalogs = manifest.Definition.Catalogs
            ?? throw Invalid("source_catalogs_invalid", "definition.catalogs must be an array.");
        EnsureUnique(bindings.Select(value => value.Name), "source_binding_duplicate", "binding");
        foreach (var binding in bindings)
        {
            ValidateIdentifier(binding.Name, "definition.bindings[].name");
            if (!string.Equals(binding.TargetKind, SourceKinds.SourceProvider, StringComparison.Ordinal))
                throw Invalid("source_binding_kind_invalid", $"Binding '{binding.Name}' must target '{SourceKinds.SourceProvider}'.");
        }
        EnsureUnique(channels.Select(value => value.Name), "source_channel_duplicate", "Channel");
        foreach (var channel in channels)
        {
            ValidateIdentifier(channel.Name, "definition.channels[].name");
            ValidateCompatibility(channel.Compatibility?.Agentstration, channel.Name);
            if (channel.Provider is null || string.IsNullOrWhiteSpace(channel.Provider.Binding)
                || !bindings.Any(binding => string.Equals(binding.Name, channel.Provider.Binding, StringComparison.Ordinal)))
                throw Invalid("source_channel_binding_invalid", $"Channel '{channel.Name}' must reference a declared Source Provider binding.");
            if (channel.Configuration is null
                || string.IsNullOrWhiteSpace(channel.Configuration.OptionSet)
                || string.IsNullOrWhiteSpace(channel.Configuration.Version)
                || string.IsNullOrWhiteSpace(channel.Configuration.SchemaDigest))
                throw Invalid("source_channel_configuration_invalid", $"Channel '{channel.Name}' requires a versioned provider configuration.");
        }
        foreach (var catalog in catalogs)
        {
            if (string.IsNullOrWhiteSpace(catalog.Kind) || string.IsNullOrWhiteSpace(catalog.Path))
                throw Invalid("source_catalog_invalid", "Catalog kind and path are required.");
            if (catalog.Kind is not (SourceCatalogKinds.Bootstrap or SourceCatalogKinds.Pack))
                throw Invalid("source_catalog_kind_unsupported", $"Catalog kind '{catalog.Kind}' is not supported.");
            _ = SourceDescendantPath.Normalize(catalog.Path, $"Catalog '{catalog.Kind}' path");
        }
        EnsureUnique(catalogs.Select(value => $"{value.Kind}:{value.Path}"), "source_catalog_duplicate", "catalog");
    }

    private static void ValidateCompatibility(SourceCompatibilityBounds? bounds, string channel)
    {
        if (bounds is null || string.IsNullOrWhiteSpace(bounds.MinVersion))
            throw Invalid("source_channel_compatibility_missing", $"Channel '{channel}' must declare compatibility.agentstration.minVersion.");
        if (!SourceSemanticVersion.TryParse(bounds.MinVersion, out var minimum))
            throw Invalid("source_compatibility_version_invalid", $"Channel '{channel}' minVersion '{bounds.MinVersion}' is not a valid Semantic Version.");
        if (bounds.MaxVersionExclusive is null) return;
        if (!SourceSemanticVersion.TryParse(bounds.MaxVersionExclusive, out var maximum))
            throw Invalid("source_compatibility_version_invalid", $"Channel '{channel}' maxVersionExclusive '{bounds.MaxVersionExclusive}' is not a valid Semantic Version.");
        if (minimum.CompareTo(maximum) >= 0)
            throw Invalid("source_compatibility_interval_invalid", $"Channel '{channel}' maxVersionExclusive must be greater than minVersion.");
    }

    private static void ValidatePortableName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !PortableNameRegex().IsMatch(value))
            throw Invalid("source_identity_invalid", $"{field} must contain 1 to 60 lowercase ASCII letters, digits or '-' and start with a letter or digit.");
    }

    private static void ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IdentifierRegex().IsMatch(value))
            throw Invalid("source_identifier_invalid", $"{field} must contain 1 to 128 ASCII letters, digits, '-' or '.' and start with a letter or digit.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string code, string label)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw Invalid(code, $"{label} '{duplicate.Key}' is declared more than once.");
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
