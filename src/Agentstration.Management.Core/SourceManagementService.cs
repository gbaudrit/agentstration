using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

    public async Task<SourceImportResult> ImportYamlAsync(string rawManifest, CancellationToken cancellationToken) =>
        await ImportYamlAsync(rawManifest, null, cancellationToken);

    public async Task<SourceImportResult> ImportYamlAsync(
        string rawManifest,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var targetScopeRef = scopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Source);
        return await scopeOperations.WriteAsync(
            ResourceKinds.Source,
            targetScopeRef,
            AuthorizationPermissions.ResourcesWrite,
            token => ReadAndImportCoreAsync(rawManifest, null, targetScopeRef, SourceRefreshTrigger.Import, token),
            cancellationToken);
    }

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
                return await ReadAndImportCoreAsync(retrieved.Content, retrieved.Origin, targetScopeRef, SourceRefreshTrigger.Import, token);
            },
            cancellationToken);
    }

    public async Task<SourceImportResult> ImportRegistryAsync(
        string rawManifest,
        SourceRegistryImportProvenance provenance,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var targetScopeRef = scopeRef ?? scopeOperations.DefaultScopeRef(ResourceKinds.Source);
        return await scopeOperations.WriteAsync(
            ResourceKinds.Source,
            targetScopeRef,
            AuthorizationPermissions.ResourcesWrite,
            async token =>
            {
                var parsed = manifests.Read(rawManifest);
                if (!string.Equals(parsed.Manifest.Definition.Publisher.Name, provenance.Selection.Publisher, StringComparison.Ordinal)
                    || !string.Equals(parsed.Manifest.Metadata.Name, provenance.Selection.SourceName, StringComparison.Ordinal)
                    || !string.Equals(parsed.Manifest.Definition.Version, provenance.Selection.Version, StringComparison.Ordinal))
                    throw Invalid("source_registry_manifest_identity_mismatch", "The retrieved SourceVersion manifest does not match the selected registry identity and version.");
                if (!string.Equals(parsed.Digest, provenance.ExpectedManifestDigest, StringComparison.Ordinal))
                    throw Invalid("source_registry_manifest_digest_mismatch", "The retrieved SourceVersion manifest does not match the selected registry canonical digest.");
                var origin = new SourceManifestOrigin
                {
                    Url = provenance.FinalManifestUrl.AbsoluteUri,
                    ETag = provenance.ManifestETag,
                    LastModified = provenance.ManifestLastModified,
                    Registry = provenance
                };
                return await ImportCoreAsync(parsed, origin, targetScopeRef, SourceRefreshTrigger.Import, token);
            },
            cancellationToken);
    }

    public async Task<SourceImportResult> RefreshExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        var source = (await GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        var gate = refreshLocks.GetOrAdd($"{scopeRef}|{publisher}|{name}", _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await scopeOperations.WriteAsync(ResourceKinds.Source, scopeRef, AuthorizationPermissions.ResourcesWrite,
                token => RefreshLockedAsync(source, trigger, token), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoredResource<SourceConfigurationResource>> UpdateRefreshConfigurationExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        SourceRefreshConfiguration refresh,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ValidateRefreshConfiguration(refresh);
        var source = (await GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        return await scopeOperations.WriteAsync(ResourceKinds.SourceConfiguration, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var address = ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Name);
            var current = await store.GetExactAsync<SourceConfigurationResource>(address, token)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceConfiguration, source.Name, source.Namespace));
            return await store.PutExactAsync(scopeRef, current.Value with
            {
                Generation = checked(current.Value.Generation + 1),
                Definition = current.Value.Definition with { Refresh = refresh }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public async Task RecordScheduledFailureExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var view = await GetExactAsync(scopeRef, publisher, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        var source = view.Source;
        await RecordAsync(source, timeProvider.GetUtcNow(), SourceImportOutcome.Rejected, null, null, null,
            view.Configuration.Definition.Origin,
            SourceRefreshTrigger.Scheduled, errorCode, errorMessage, cancellationToken);
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

    public async Task DeleteExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ValidatePortableName(publisher, "publisher");
        ValidatePortableName(name, "metadata.name");
        var address = ScopedResourceAddress.Create(
            scopeRef, new ResourceNamespace(publisher), ResourceKinds.Source, name);
        var source = await store.GetExactAsync<SourceResource>(address, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, address.Namespace));
        if (!string.Equals(source.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The Source changed since it was loaded.");

        await scopeOperations.WriteAsync(
            ResourceKinds.Source,
            scopeRef,
            AuthorizationPermissions.ResourcesDelete,
            async token =>
            {
                await DeleteChildrenAsync<SourceChannelObservedResource>(scopeRef, ResourceKinds.SourceChannelObservedState, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceChannelRefreshRecordResource>(scopeRef, ResourceKinds.SourceChannelRefreshRecord, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceChannelSnapshotResource>(scopeRef, ResourceKinds.SourceChannelSnapshot, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceImportRecordResource>(scopeRef, ResourceKinds.SourceImportRecord, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceConfigurationResource>(scopeRef, ResourceKinds.SourceConfiguration, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceObservedResource>(scopeRef, ResourceKinds.SourceObservedState, source.Value.Uid, value => value.Definition.SourceUid, token);
                await DeleteChildrenAsync<SourceVersionResource>(scopeRef, ResourceKinds.SourceVersion, source.Value.Uid, value => value.Definition.SourceUid, token);
                await store.DeleteExactAsync(address, ifMatch, token);
                return true;
            },
            cancellationToken);
    }

    private async Task DeleteChildrenAsync<T>(
        ResourceScopeRef scopeRef,
        string kind,
        Guid sourceUid,
        Func<T, Guid> sourceUidSelector,
        CancellationToken cancellationToken)
        where T : Resource
    {
        var children = await store.ListExactAsync<T>(scopeRef, kind, 0, int.MaxValue, cancellationToken);
        foreach (var child in children.Where(value => sourceUidSelector(value.Value) == sourceUid))
        {
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, child.Value.Namespace, kind, child.Value.Name),
                child.ETag,
                cancellationToken);
        }
    }

    private async Task<SourceImportResult> ReadAndImportCoreAsync(
        string rawManifest,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ImportCoreAsync(manifests.Read(rawManifest), origin, scopeRef, trigger, cancellationToken);
        }
        catch (SourceValidationException exception) when (exception.ParsedManifest is { } parsed)
        {
            await TryRecordRejectedAsync(parsed, origin, scopeRef, trigger, exception, cancellationToken);
            throw;
        }
    }

    private async Task<SourceImportResult> ImportCoreAsync(
        ParsedSourceManifest parsed,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
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
            await RecordAsync(source, now, SourceImportOutcome.Rejected, manifest.Definition.Version, parsed.Digest, null, origin, trigger,
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

        else if (origin is not null && !EquivalentOrigin(configuration.Value.Definition.Origin, origin))
        {
            configuration = await store.PutExactAsync(scopeRef, configuration.Value with
            {
                Generation = checked(configuration.Value.Generation + 1),
                Definition = configuration.Value.Definition with { Origin = origin }
            }, configuration.ETag, false, cancellationToken);
        }

        await RecordAsync(source, now, outcome, manifest.Definition.Version, parsed.Digest, version.Uid, origin, trigger, null, null, cancellationToken);
        var view = await BuildViewAsync(source, cancellationToken);
        return new SourceImportResult(view, version, outcome, await verification.VerifyDefinitionAsync(version, cancellationToken));
    }

    private async Task TryRecordRejectedAsync(
        ParsedSourceManifest parsed,
        SourceManifestOrigin? origin,
        ResourceScopeRef scopeRef,
        SourceRefreshTrigger trigger,
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
            trigger,
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
        SourceRefreshTrigger trigger,
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
            ErrorMessage = errorMessage,
            LastTrigger = trigger,
            ConsecutiveFailures = errorCode is null ? 0 : checked((current?.Value.Definition.ConsecutiveFailures ?? 0) + 1)
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
                Trigger = trigger,
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

    private async Task<SourceImportResult> RefreshLockedAsync(
        SourceResource source,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var configurationAddress = ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Name);
        var configuration = await store.GetExactAsync<SourceConfigurationResource>(configurationAddress, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceConfiguration, source.Name, source.Namespace));
        if (configuration.Value.Definition.Origin is not { } origin
            || !Uri.TryCreate(origin.Url, UriKind.Absolute, out var uri))
            throw Invalid("source_origin_missing", "The Source has no associated HTTP(S) origin.");
        if (origin.Registry is not null)
            throw Invalid("source_registry_origin_requires_exact_import", "A Source imported from a registry can only change through another exact registry observation import.");

        RetrievedSourceManifest retrieved;
        try
        {
            retrieved = await retrieval.RetrieveAsync(uri, origin, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                SourceValidationException value => value.Code,
                SourceRetrievalException value => value.Code,
                _ => "source_refresh_failed"
            };
            await RecordAsync(source, timeProvider.GetUtcNow(), SourceImportOutcome.Rejected, null, null, null,
                origin, trigger, code, exception.Message, cancellationToken);
            throw;
        }

        if (!retrieved.NotModified)
            return await ReadAndImportCoreAsync(retrieved.Content, retrieved.Origin, scopeRef, trigger, cancellationToken);

        if (configuration.Value.Definition.Origin != retrieved.Origin)
            _ = await store.PutExactAsync(scopeRef, configuration.Value with
            {
                Generation = checked(configuration.Value.Generation + 1),
                Definition = configuration.Value.Definition with { Origin = retrieved.Origin }
            }, configuration.ETag, false, cancellationToken);

        var observed = await store.GetExactAsync<SourceObservedResource>(
            ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceObservedState, source.Name), cancellationToken)
            ?? throw new InvalidOperationException($"Source '{source.Address}' has no observed state.");
        var versionUid = observed.Value.Definition.LastSuccessfulVersionUid
            ?? throw new InvalidOperationException($"Source '{source.Address}' has no successful Source Version.");
        var version = await GetVersionExactAsync(scopeRef, source.Definition.Publisher, source.Name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
        await RecordAsync(source, timeProvider.GetUtcNow(), SourceImportOutcome.Unchanged,
            version.Definition.Version, version.Definition.ManifestDigest, version.Uid, retrieved.Origin, trigger, null, null, cancellationToken);
        return new(await BuildViewAsync(source, cancellationToken), version, SourceImportOutcome.Unchanged,
            await verification.VerifyDefinitionAsync(version, cancellationToken));
    }

    private static void ValidateRefreshConfiguration(SourceRefreshConfiguration refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ValidatePolicy(refresh.Source, "refresh.source");
        ValidatePolicy(refresh.Channels, "refresh.channels");
        foreach (var (channel, policy) in refresh.ChannelOverrides)
        {
            if (string.IsNullOrWhiteSpace(channel) || channel.Length > 100)
                throw Invalid("source_refresh_channel_invalid", "Channel override names must contain 1 to 100 characters.");
            ValidatePolicy(policy, $"refresh.channelOverrides.{channel}");
        }
    }

    private static void ValidatePolicy(SourceRefreshPolicy policy, string field)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.IntervalSeconds is < 60 or > 604_800)
            throw Invalid("source_refresh_interval_invalid", $"{field}.intervalSeconds must be between 60 and 604800.");
        if (policy.TimeoutSeconds is < 1 or > 300)
            throw Invalid("source_refresh_timeout_invalid", $"{field}.timeoutSeconds must be between 1 and 300.");
        if (policy.MaximumAttempts is < 1 or > 10)
            throw Invalid("source_refresh_attempts_invalid", $"{field}.maximumAttempts must be between 1 and 10.");
        if (policy.InitialBackoffSeconds is < 1 or > 3600
            || policy.MaximumBackoffSeconds < policy.InitialBackoffSeconds
            || policy.MaximumBackoffSeconds > 86_400)
            throw Invalid("source_refresh_backoff_invalid", $"{field} backoff must be ordered and between 1 and 86400 seconds.");
        if (policy.JitterSeconds < 0 || policy.JitterSeconds > Math.Min(3600, policy.IntervalSeconds / 2))
            throw Invalid("source_refresh_jitter_invalid", $"{field}.jitterSeconds must be between 0 and half the interval, capped at 3600.");
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

    private static bool EquivalentOrigin(SourceManifestOrigin? current, SourceManifestOrigin candidate)
    {
        if (current is null) return false;
        if (current.Registry is null || candidate.Registry is null) return current == candidate;
        return current.Registry.Selection == candidate.Registry.Selection
            && string.Equals(current.Registry.ExpectedManifestDigest,
                candidate.Registry.ExpectedManifestDigest, StringComparison.Ordinal)
            && current.Registry.FinalManifestUrl == candidate.Registry.FinalManifestUrl;
    }

    private static void ValidatePortableName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !PortableNameRegex().IsMatch(value))
            throw Invalid("source_identity_invalid", $"{field} must contain 1 to 60 lowercase ASCII letters, digits or '-' and start with a letter or digit.");
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
}
