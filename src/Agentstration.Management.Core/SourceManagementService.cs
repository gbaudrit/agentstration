using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class SourceManagementService(
    IControlPlaneStore store,
    IRequestContextScopeFactory scopes,
    ISourceManifestReader manifests,
    ISourceManifestRetriever retrieval,
    TimeProvider timeProvider,
    ResourceScopeOperationService scopeOperations,
    SourceVerificationService verification)
{
    public async Task<SourceImportResult> ImportYamlAsync(string rawManifest, CancellationToken cancellationToken) =>
        await ImportYamlAsync(rawManifest, null, cancellationToken);

    public async Task<SourceImportResult> ImportYamlAsync(
        string rawManifest,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken) =>
        await ImportAsync(
            manifests.Read(rawManifest),
            null,
            scopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Source),
            cancellationToken);

    public async Task<SourceImportResult> ImportUrlAsync(Uri source, CancellationToken cancellationToken)
        => await ImportUrlAsync(source, null, cancellationToken);

    public async Task<SourceImportResult> ImportUrlAsync(
        Uri source,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var targetScopeRef = scopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Source);
        return await scopeOperations.WriteAsync(
            ResourceKinds.Source,
            targetScopeRef,
            AuthorizationPermissions.ResourcesWrite,
            async token =>
            {
                var retrieved = await retrieval.RetrieveAsync(source, token);
                return await ImportCoreAsync(manifests.Read(retrieved.Content), retrieved.Origin, targetScopeRef, token);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<SourceView>> ListAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var sources = await store.ListAllAsync<SourceResource>(ResourceKinds.Source, cancellationToken);
        var configurations = await store.ListAllAsync<SourceConfigurationResource>(ResourceKinds.SourceConfiguration, cancellationToken);
        var observations = await store.ListAllAsync<SourceObservedResource>(ResourceKinds.SourceObservedState, cancellationToken);
        var versions = await store.ListAllAsync<SourceVersionResource>(ResourceKinds.SourceVersion, cancellationToken);
        return sources.Select(source => new SourceView(
            source.Value,
            configurations.Single(value => value.Value.Definition.SourceUid == source.Value.Uid).Value,
            observations.Single(value => value.Value.Definition.SourceUid == source.Value.Uid).Value,
            versions.Count(value => value.Value.Definition.SourceUid == source.Value.Uid)))
            .OrderBy(value => value.Source.Definition.Publisher, StringComparer.Ordinal)
            .ThenBy(value => value.Source.Metadata.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<SourceView?> GetAsync(string publisher, string name, CancellationToken cancellationToken)
    {
        ValidatePortableName(publisher, "publisher");
        ValidatePortableName(name, "metadata.name");
        using var system = scopes.PushSystem();
        var @namespace = new ResourceNamespace(publisher);
        var source = await store.GetAsync<SourceResource>(new(ResourceKinds.Source, name, @namespace), cancellationToken);
        if (source is null) return null;
        return await BuildViewAsync(source.Value, cancellationToken);
    }

    public async Task<SourceView?> GetExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        CancellationToken cancellationToken)
    {
        ValidatePortableName(publisher, "publisher");
        ValidatePortableName(name, "metadata.name");
        using var system = scopes.PushSystem();
        var @namespace = new ResourceNamespace(publisher);
        var source = await store.GetExactAsync<SourceResource>(
            ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.Source, name),
            cancellationToken);
        return source is null ? null : await BuildViewAsync(source.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceVersionResource>> ListVersionsAsync(string publisher, string name, CancellationToken cancellationToken)
    {
        var source = await GetRequiredAsync(publisher, name, cancellationToken);
        return await ListVersionsAsync(source, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceVersionResource>> ListVersionsExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        CancellationToken cancellationToken)
    {
        var source = (await GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        return await ListVersionsAsync(source, cancellationToken);
    }

    private async Task<IReadOnlyList<SourceVersionResource>> ListVersionsAsync(SourceResource source, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        using var system = scopes.PushSystem();
        return (await store.ListExactAsync<SourceVersionResource>(scopeRef, ResourceKinds.SourceVersion, 0, int.MaxValue, cancellationToken))
            .Where(value => value.Value.Definition.SourceUid == source.Uid)
            .OrderByDescending(value => value.Value.Definition.ImportedAt)
            .Select(value => value.Value)
            .ToArray();
    }

    public async Task<SourceVersionResource?> GetVersionAsync(string publisher, string name, Guid versionUid, CancellationToken cancellationToken) =>
        (await ListVersionsAsync(publisher, name, cancellationToken)).SingleOrDefault(value => value.Uid == versionUid);

    public async Task<SourceVersionResource?> GetVersionExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        CancellationToken cancellationToken) =>
        (await ListVersionsExactAsync(scopeRef, publisher, name, cancellationToken)).SingleOrDefault(value => value.Uid == versionUid);

    public async Task<StoredResource<SourceConfigurationResource>> UpdateDisplayNameAsync(
        string publisher,
        string name,
        string displayName,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw Invalid("source_display_name_invalid", "Display name must contain 1 to 200 characters.");
        var source = await GetRequiredAsync(publisher, name, cancellationToken);
        return await UpdateDisplayNameAsync(source, displayName, ifMatch, cancellationToken);
    }

    public async Task<StoredResource<SourceConfigurationResource>> UpdateDisplayNameExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        string displayName,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw Invalid("source_display_name_invalid", "Display name must contain 1 to 200 characters.");
        var source = (await GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        return await UpdateDisplayNameAsync(source, displayName, ifMatch, cancellationToken);
    }

    private async Task<StoredResource<SourceConfigurationResource>> UpdateDisplayNameAsync(
        SourceResource source,
        string displayName,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        return await scopeOperations.WriteAsync(ResourceKinds.SourceConfiguration, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var address = ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Metadata.Name);
            var current = await store.GetExactAsync<SourceConfigurationResource>(address, token)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceConfiguration, source.Metadata.Name, source.Namespace));
            return await store.PutExactAsync(scopeRef, current.Value with
            {
                Generation = checked(current.Value.Generation + 1),
                Definition = current.Value.Definition with { DisplayName = displayName.Trim() }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    private async Task<SourceImportResult> ImportAsync(
        ParsedSourceManifest parsed,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(
            ResourceKinds.Source,
            scopeRef,
            AuthorizationPermissions.ResourcesWrite,
            token => ImportCoreAsync(parsed, origin, scopeRef, token),
            cancellationToken);

    private async Task<SourceImportResult> ImportCoreAsync(
        ParsedSourceManifest parsed,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        try
        {
            Validate(parsed.Manifest);
        }
        catch (SourceValidationException exception)
        {
            await TryRecordRejectedAsync(parsed, origin, scopeRef, exception, cancellationToken);
            throw;
        }
        var manifest = parsed.Manifest;
        var now = timeProvider.GetUtcNow();
        var @namespace = new ResourceNamespace(manifest.Definition.Publisher.Name);
        var sourceAddress = ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.Source, manifest.Metadata.Name);
        var storedSource = await store.GetExactAsync<SourceResource>(sourceAddress, cancellationToken);
        if (storedSource is null)
        {
            storedSource = await store.CreateImmutableAsync(new SourceResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Source,
                Metadata = new ResourceMetadata { Name = manifest.Metadata.Name, Namespace = @namespace },
                ScopeRef = scopeRef,
                Definition = new SourceIdentityProperties { Publisher = manifest.Definition.Publisher.Name },
                Status = Succeeded()
            }, cancellationToken);
        }

        var source = storedSource.Value;
        var versions = (await store.ListExactAsync<SourceVersionResource>(scopeRef, ResourceKinds.SourceVersion, 0, int.MaxValue, cancellationToken))
            .Where(value => value.Value.Definition.SourceUid == source.Uid)
            .ToArray();
        var sameVersion = versions.SingleOrDefault(value => string.Equals(value.Value.Definition.Version, manifest.Definition.Version, StringComparison.Ordinal));
        if (sameVersion is not null && !string.Equals(sameVersion.Value.Definition.ManifestDigest, parsed.Digest, StringComparison.Ordinal))
        {
            await RecordAsync(source, now, SourceImportOutcome.Rejected, manifest.Definition.Version, parsed.Digest, null, origin,
                "source_version_digest_conflict", "The declared Source Version was already imported with a different manifest digest.", cancellationToken);
            throw new SourceVersionConflictException($"Source '{manifest.Definition.Publisher.Name}/{manifest.Metadata.Name}' version '{manifest.Definition.Version}' was already imported with a different manifest digest.");
        }

        SourceVersionResource version;
        SourceImportOutcome outcome;
        if (sameVersion is not null)
        {
            version = sameVersion.Value;
            outcome = SourceImportOutcome.Unchanged;
        }
        else
        {
            var versionName = VersionResourceName(manifest.Metadata.Name, manifest.Definition.Version);
            var storedVersion = await store.CreateImmutableAsync(new SourceVersionResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.SourceVersion,
                Metadata = new ResourceMetadata { Name = versionName, Namespace = @namespace },
                ScopeRef = scopeRef,
                Definition = new SourceVersionProperties
                {
                    SourceUid = source.Uid,
                    SourceName = source.Metadata.Name,
                    Publisher = source.Definition.Publisher,
                    Version = manifest.Definition.Version,
                    ManifestDigest = parsed.Digest,
                    RawManifest = parsed.RawManifest,
                    PublishedDefinition = manifest.Definition,
                    ImportedAt = now,
                    Origin = origin
                },
                Status = Succeeded()
            }, cancellationToken);
            version = storedVersion.Value;
            outcome = SourceImportOutcome.Created;
        }

        var configurationAddress = ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.SourceConfiguration, source.Metadata.Name);
        var configuration = await store.GetExactAsync<SourceConfigurationResource>(configurationAddress, cancellationToken);
        if (configuration is null)
        {
            configuration = await store.PutExactAsync(scopeRef, new SourceConfigurationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.SourceConfiguration,
                Metadata = new ResourceMetadata { Name = source.Metadata.Name, Namespace = @namespace },
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = new SourceConfigurationProperties
                {
                    SourceUid = source.Uid,
                    DisplayName = string.IsNullOrWhiteSpace(manifest.Definition.DisplayName) ? source.Metadata.Name : manifest.Definition.DisplayName.Trim(),
                    Origin = origin
                },
                Status = Succeeded()
            }, null, true, cancellationToken);
        }

        await RecordAsync(source, now, outcome, manifest.Definition.Version, parsed.Digest, version.Uid, origin, null, null, cancellationToken);
        var view = await BuildViewAsync(source, cancellationToken);
        return new SourceImportResult(view, version, outcome, await verification.VerifyDefinitionAsync(version, cancellationToken));
    }

    private async Task TryRecordRejectedAsync(
        ParsedSourceManifest parsed,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        SourceValidationException exception,
        CancellationToken cancellationToken)
    {
        var manifest = parsed.Manifest;
        var publisher = manifest.Definition?.Publisher?.Name;
        var name = manifest.Metadata?.Name;
        if (publisher is null || name is null || !PortableNameRegex().IsMatch(publisher) || !PortableNameRegex().IsMatch(name)) return;
        var source = await store.GetExactAsync<SourceResource>(
            ScopedResourceAddress.Create(scopeRef, new ResourceNamespace(publisher), ResourceKinds.Source, name),
            cancellationToken);
        if (source is null) return;
        await RecordAsync(
            source.Value,
            timeProvider.GetUtcNow(),
            SourceImportOutcome.Rejected,
            manifest.Definition?.Version,
            parsed.Digest,
            null,
            origin,
            exception.Code,
            exception.Message,
            cancellationToken);
    }

    private async Task RecordAsync(
        SourceResource source,
        DateTimeOffset attemptedAt,
        SourceImportOutcome outcome,
        string? declaredVersion,
        string? digest,
        Guid? versionUid,
        SourceManifestOrigin? origin,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var observedAddress = ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceObservedState, source.Metadata.Name);
        var current = await store.GetExactAsync<SourceObservedResource>(observedAddress, cancellationToken);
        var properties = new SourceObservedProperties
        {
            SourceUid = source.Uid,
            LastAttemptAt = attemptedAt,
            LastOutcome = outcome,
            LastSuccessfulVersionUid = versionUid ?? current?.Value.Definition.LastSuccessfulVersionUid,
            LastSuccessfulAt = versionUid is null ? current?.Value.Definition.LastSuccessfulAt : attemptedAt,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
        _ = await store.PutExactAsync(scopeRef, new SourceObservedResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceObservedState,
            Metadata = new ResourceMetadata { Name = source.Metadata.Name, Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Generation = current is null ? 1 : checked(current.Value.Generation + 1),
            Definition = properties,
            Status = errorCode is null ? Succeeded() : Failed(errorCode, errorMessage!)
        }, current?.ETag, current is null, cancellationToken);

        _ = await store.CreateImmutableAsync(new SourceImportRecordResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceImportRecord,
            Metadata = new ResourceMetadata { Name = $"import-{Guid.NewGuid():N}", Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Definition = new SourceImportRecordProperties
            {
                SourceUid = source.Uid,
                AttemptedAt = attemptedAt,
                Outcome = outcome,
                DeclaredVersion = declaredVersion,
                ManifestDigest = digest,
                SourceVersionUid = versionUid,
                Origin = origin,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            },
            Status = errorCode is null ? Succeeded() : Failed(errorCode, errorMessage!)
        }, cancellationToken);
    }

    private async Task<SourceView> BuildViewAsync(SourceResource source, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var configuration = await store.GetExactAsync<SourceConfigurationResource>(
            ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Metadata.Name),
            cancellationToken)
            ?? throw new InvalidOperationException($"Source '{source.Definition.Publisher}/{source.Metadata.Name}' has no local configuration.");
        var observed = await store.GetExactAsync<SourceObservedResource>(
            ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceObservedState, source.Metadata.Name),
            cancellationToken)
            ?? throw new InvalidOperationException($"Source '{source.Definition.Publisher}/{source.Metadata.Name}' has no observed state.");
        var count = (await store.ListExactAsync<SourceVersionResource>(scopeRef, ResourceKinds.SourceVersion, 0, int.MaxValue, cancellationToken))
            .Count(value => value.Value.Definition.SourceUid == source.Uid);
        return new SourceView(source, configuration.Value, observed.Value, count);
    }

    private async Task<SourceResource> GetRequiredAsync(string publisher, string name, CancellationToken cancellationToken) =>
        (await GetAsync(publisher, name, cancellationToken))?.Source
        ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));

    private static ResourceScopeRef RequireScope(Resource resource) =>
        resource.ScopeRef ?? throw new InvalidOperationException($"Resource '{resource.Address}' has no ownership scope.");

    private static void Validate(PublishedSourceVersionManifest manifest)
    {
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
            SourceChannelCompatibilityEvaluator.Validate(channel.Compatibility?.Agentstration, channel.Name);
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

    private static string VersionResourceName(string sourceName, string version)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..20];
        return $"{sourceName}-v-{key}";
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static ResourceStatus Failed(string code, string message) => new()
    {
        ProvisioningState = ProvisioningState.Failed,
        Conditions = [new ResourceCondition { Type = "Imported", Status = "False", Reason = code, Message = message }]
    };
    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableNameRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
