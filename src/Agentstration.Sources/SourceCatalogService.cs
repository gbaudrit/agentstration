using System.Globalization;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Sources;

public sealed class SourceCatalogService(
    SourceManagementService sources,
    SourceChannelSnapshotService snapshots,
    SourceChannelCompatibilityEvaluator compatibility,
    ISourceSnapshotContentReader contentReader,
    ISourceCatalogManifestReader manifests,
    IEnumerable<ISourceCatalogHandler> catalogHandlers,
    ResourceScopeOperationService scopeOperations)
{
    public async Task<IReadOnlyList<SourceCatalogView>> BrowseAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string sourceName,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string? locale,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(SourceResourceKinds.SourceChannelSnapshot, scopeRef, AuthorizationPermissions.ResourcesRead, async token =>
        {
            var source = (await sources.GetExactAsync(scopeRef, publisher, sourceName, token))?.Source
                ?? throw new ResourceNotFoundException(new(SourceResourceKinds.Source, sourceName, new ResourceNamespace(publisher)));
            var version = await sources.GetVersionExactAsync(scopeRef, publisher, sourceName, versionUid, token)
                ?? throw new ResourceNotFoundException(new(SourceResourceKinds.SourceVersion, versionUid.ToString("D")));
            var channelDefinition = version.Definition.PublishedDefinition.Channels.SingleOrDefault(value =>
                string.Equals(value.Name, channel, StringComparison.Ordinal))
                ?? throw Invalid("source_channel_missing", $"Source Version '{version.Definition.Version}' has no channel named '{channel}'.");
            compatibility.RequireCompatible(channelDefinition);
            var snapshot = await snapshots.GetAsync(scopeRef, publisher, sourceName, versionUid, channel, snapshotUid, token)
                ?? throw new ResourceNotFoundException(new(SourceResourceKinds.SourceChannelSnapshot, snapshotUid.ToString("D")));
            var requestedLocale = ValidateRequestedLocale(locale);
            await using var content = await contentReader.OpenAsync(snapshot.Definition.Artifact, token);
            var results = new List<SourceCatalogView>(version.Definition.PublishedDefinition.Catalogs.Count);
            var catalogNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in version.Definition.PublishedDefinition.Catalogs)
            {
                var catalogPath = SourceDescendantPath.Normalize(declaration.Path, $"Catalog '{declaration.Kind}' path");
                var raw = await content.ReadTextAsync(catalogPath, SourceCatalogLimits.MaximumManifestBytes, token);
                var parsed = manifests.ReadCatalog(raw, declaration.Kind);
                if (!catalogNames.Add(parsed.Name))
                    throw Invalid("source_catalog_name_duplicate", $"Catalog metadata.name '{parsed.Name}' is declared more than once in Source Version '{version.Definition.Version}'.");
                var provenance = new SourceCatalogProvenance(
                    source.Uid, version.Uid, version.Definition.Version, channel, snapshot.Uid,
                    snapshot.Definition.Artifact.Sha256, parsed.Kind, parsed.Name, catalogPath);
                if (string.Equals(parsed.Kind, SourceCatalogKinds.Bootstrap, StringComparison.Ordinal))
                {
                    results.Add(await BootstrapAsync(
                        content, provenance, manifests.ReadBootstrapCatalog(parsed), catalogPath, requestedLocale, token));
                    continue;
                }

                var handler = catalogHandlers.SingleOrDefault(candidate =>
                    string.Equals(candidate.Kind, parsed.Kind, StringComparison.Ordinal))
                    ?? throw Invalid("source_catalog_kind_unsupported", $"Catalog kind '{parsed.Kind}' is not supported.");
                results.Add(await handler.BrowseAsync(
                    parsed, content, provenance, catalogPath, requestedLocale, token));
            }
            return results;
        }, cancellationToken);

    private async Task<SourceCatalogView> BootstrapAsync(
        ISourceSnapshotContent content,
        SourceCatalogProvenance provenance,
        BootstrapCatalogManifest catalog,
        string catalogPath,
        string? requestedLocale,
        CancellationToken cancellationToken)
    {
        var entries = new List<SourceBootstrapEntryView>(catalog.Definition.Entries.Count);
        var catalogDirectory = SourceDescendantPath.Parent(catalogPath);
        foreach (var entry in catalog.Definition.Entries)
        {
            SourceBootstrapProfileContract? invariant = null;
            var variants = new List<SourceBootstrapVariantView>(entry.Variants.Count);
            foreach (var variant in entry.Variants)
            {
                var variantPath = SourceDescendantPath.Normalize(variant.Path, $"Bootstrap variant '{entry.Name}/{variant.Locale}' path");
                var expected = $"profiles/{entry.Name}/{variant.Locale}";
                if (!string.Equals(variantPath, expected, StringComparison.Ordinal))
                    throw Invalid("source_bootstrap_variant_layout_invalid", $"Bootstrap variant '{entry.Name}/{variant.Locale}' must use path '{expected}'.");
                var resolvedPath = SourceDescendantPath.Combine(catalogDirectory, variantPath, $"Bootstrap variant '{entry.Name}/{variant.Locale}' path");
                var descriptorPath = SourceDescendantPath.Combine(resolvedPath, "profile.yaml", $"Bootstrap variant '{entry.Name}/{variant.Locale}' descriptor");
                var descriptor = manifests.ReadBootstrapProfile(await content.ReadTextAsync(
                    descriptorPath, SourceCatalogLimits.MaximumManifestBytes, cancellationToken));
                if (!string.Equals(descriptor.Name, entry.Name, StringComparison.Ordinal))
                    throw Invalid("source_bootstrap_profile_name_mismatch", $"Variant '{entry.Name}/{variant.Locale}' contains Bootstrap Profile '{descriptor.Name}'.");
                if (invariant is null) invariant = descriptor;
                else if (invariant.TargetScope != descriptor.TargetScope || !invariant.Bindings.SequenceEqual(descriptor.Bindings))
                    throw Invalid("source_bootstrap_variant_contract_mismatch", $"Bootstrap variants for '{entry.Name}' must use the same target scope and binding declarations.");
                variants.Add(new(variant.Locale, resolvedPath));
            }
            var resolvedLocale = requestedLocale is not null && variants.Any(value => value.Locale == requestedLocale)
                ? requestedLocale
                : entry.DefaultLocale;
            var resolved = variants.Single(value => value.Locale == resolvedLocale);
            entries.Add(new(entry.Name, entry.DefaultLocale, variants, resolved.Locale, resolved.Path));
        }
        return new(provenance, catalog.Definition.DisplayName, catalog.Definition.Description, entries, []);
    }

    private static string? ValidateRequestedLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        if (locale == "neutral") return locale;
        string canonical;
        try
        {
            canonical = CultureInfo.GetCultureInfo(locale).Name;
        }
        catch (CultureNotFoundException)
        {
            throw Invalid("source_catalog_locale_invalid", $"Requested locale '{locale}' must be a canonical BCP 47 identifier or 'neutral'.");
        }
        if (canonical.Length == 0 || canonical != locale)
            throw Invalid("source_catalog_locale_invalid", $"Requested locale '{locale}' must be a canonical BCP 47 identifier or 'neutral'.");
        return locale;
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
