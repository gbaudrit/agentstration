using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceRegistryDiscoveryService(
    IControlPlaneStore store,
    ISourceRegistryCacheStore cache,
    ISourceRegistryIndexReader indexReader,
    ISourceRegistryReader registryReader,
    ISourceRegistryReferenceResolver references,
    ISourceRegistryDocumentRetriever documents,
    IAgentstrationVersionProvider versions,
    SourceRegistryTrustEvaluationService trust,
    SourceManagementService sources,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public async Task<SourceRegistryDiscoveryPage> SearchAsync(
        SourceRegistryDiscoveryQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        var candidates = await LoadCurrentAsync(cancellationToken);
        var sourceGroups = candidates
            .Where(value => MatchesText(value, query))
            .GroupBy(value => (value.Source.Publisher, value.Source.Name))
            .OrderBy(value => value.Key.Publisher, StringComparer.Ordinal)
            .ThenBy(value => value.Key.Name, StringComparer.Ordinal)
            .ToArray();
        var discovered = new List<SourceRegistryDiscoverySource>(sourceGroups.Length);
        foreach (var sourceGroup in sourceGroups)
        {
            var versionViews = new List<SourceRegistryDiscoveryVersion>();
            foreach (var versionGroup in sourceGroup.GroupBy(value => value.Version.Version)
                         .OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var observations = versionGroup
                    .Where(value => MatchesObservation(value, query))
                    .OrderBy(value => value.Registration.Name, StringComparer.Ordinal)
                    .ThenBy(value => value.Catalog.Name, StringComparer.Ordinal)
                    .ThenBy(value => value.Version.ManifestDigest, StringComparer.Ordinal)
                    .Select(ToObservation)
                    .ToArray();
                if (observations.Length == 0) continue;
                var conflicted = observations.Select(value => value.ManifestDigest)
                    .Distinct(StringComparer.Ordinal).Skip(1).Any();
                var evaluated = await trust.EvaluateSourceAsync(
                    sourceGroup.Key.Publisher, sourceGroup.Key.Name, versionGroup.Key, null, cancellationToken);
                if (query.ConflictsOnly && !conflicted) continue;
                if (query.PublisherStatus is { } publisherStatus
                    && evaluated.Publisher.EffectiveStatus != publisherStatus) continue;
                if (query.VerificationStatus is { } verificationStatus
                    && evaluated.VersionStatus != verificationStatus) continue;
                versionViews.Add(new(versionGroup.Key, conflicted, evaluated.VersionStatus,
                    evaluated.VersionReasonCode, observations));
            }
            if (versionViews.Count == 0) continue;
            var representative = sourceGroup.OrderBy(value => value.Registration.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Catalog.Name, StringComparer.Ordinal).First().Source;
            discovered.Add(new(sourceGroup.Key.Publisher, sourceGroup.Key.Name,
                representative.DisplayName, representative.Description, versionViews));
        }

        var total = discovered.Count;
        var page = discovered.Skip(query.Skip).Take(query.Take).ToArray();
        return new(page, page.Length, total, query.Skip, query.Take);
    }

    public async Task<SourceRegistryDiscoverySource?> GetAsync(
        string publisher,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var page = await SearchAsync(new SourceRegistryDiscoveryQuery
        {
            Publisher = publisher,
            Search = sourceName,
            CompatibleOnly = false,
            Take = 100
        }, cancellationToken);
        return page.Value.SingleOrDefault(value =>
            string.Equals(value.Publisher, publisher, StringComparison.Ordinal)
            && string.Equals(value.Name, sourceName, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<SourceRegistryDiscoveryPublisher>> ListPublishersAsync(
        CancellationToken cancellationToken)
    {
        var candidates = await LoadCurrentAsync(cancellationToken);
        var result = new List<SourceRegistryDiscoveryPublisher>();
        foreach (var group in candidates.GroupBy(value => value.Source.Publisher)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var evaluated = await trust.EvaluateSourceAsync(group.Key, group.First().Source.Name,
                group.First().Version.Version, null, cancellationToken);
            var publisher = group.OrderBy(value => value.Registration.Name, StringComparer.Ordinal).First().Publisher;
            result.Add(new(group.Key, publisher.DisplayName, publisher.Url,
                evaluated.Publisher.EffectiveStatus,
                group.Select(value => value.Source.Name).Distinct(StringComparer.Ordinal).Count(),
                group.Count()));
        }
        return result;
    }

    public async Task<SourceImportResult> ImportAsync(
        SourceRegistryObservationSelection selection,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var actor = requestContext.IsInitialized ? requestContext.Current.PrincipalId : (Guid?)null;
        try
        {
            var selected = await ResolveSelectionAsync(selection, cancellationToken);
            if (!selected.Registration.Definition.Enabled)
                throw Failure("source_registry_selection_disabled", "The selected registry registration is disabled.");
            if (selected.Registration.Definition.TrustPolicy == SourceRegistryTrustPolicy.Untrusted)
                throw Failure("source_registry_selection_policy_denied", "The selected registry policy does not permit exact Source import.");
            if (selected.Conflicted)
                throw Failure("source_registry_selection_conflicted", "The selected Source version has conflicting manifest digests.");

            var evaluated = await trust.EvaluateSourceAsync(selection.Publisher, selection.SourceName,
                selection.Version, selected.Version.ManifestDigest, cancellationToken);
            if (evaluated.VersionStatus == SourceVerificationStatus.Revoked)
                throw Failure("source_registry_selection_revoked", "The selected Source publisher is revoked.");
            if (evaluated.VersionStatus == SourceVerificationStatus.Conflict)
                throw Failure("source_registry_selection_conflicted", "The selected Source version has conflicting trusted evidence.");
            var acceptedPublisher = ParseStatus(selected.Publisher.Status) switch
            {
                SourceRegistryPublisherStatus.Revoked => throw Failure("source_registry_selection_revoked", "The selected Source publisher is revoked."),
                SourceRegistryPublisherStatus.Verified => true,
                SourceRegistryPublisherStatus.Official => true,
                _ => false
            };
            if (!acceptedPublisher)
                throw Failure("source_registry_selection_policy_denied", "The selected registry does not provide an accepted publisher assertion.");

            var manifestUrl = references.ResolveManifestUrl(selected.Observation.FinalIndexUrl,
                selected.Version.ManifestUrl);
            RetrievedSourceRegistryDocument retrieved;
            try
            {
                retrieved = await documents.RetrieveAsync(manifestUrl, null, null,
                    SourceRegistryLimits.MaximumIndexDocumentBytes, RetrievalContext(selected.Registration), cancellationToken);
            }
            catch (SourceRetrievalException exception)
            {
                throw new SourceRegistryOperationException(exception.Code, exception.Message, true, exception);
            }
            if (retrieved.NotModified || retrieved.Content is null)
                throw Failure("source_registry_manifest_empty", "The selected SourceVersion manifest returned no content.", true);

            var provenance = new SourceRegistryImportProvenance
            {
                Selection = selection,
                RegistrationName = selected.Registration.Name,
                ConfiguredIndexUrl = selected.Observation.RequestedIndexUrl,
                RequestedIndexUrl = selected.Observation.RequestedIndexUrl,
                FinalIndexUrl = selected.Observation.FinalIndexUrl,
                IndexDigest = selected.Observation.IndexDigest,
                CatalogDigest = selected.Catalog.RegistryDigest,
                CatalogRegistryUrl = selected.Catalog.RegistryUrl,
                Catalog = selected.Catalog,
                FetchedAt = selected.Observation.FetchedAt,
                IndexETag = selected.Observation.ETag,
                IndexLastModified = selected.Observation.LastModified,
                Publisher = selected.Publisher,
                Source = selected.Source,
                Version = selected.Version,
                ManifestUrl = selected.Version.ManifestUrl,
                FinalManifestUrl = retrieved.FinalUrl,
                ManifestETag = retrieved.ETag,
                ManifestLastModified = retrieved.LastModified,
                ExpectedManifestDigest = selected.Version.ManifestDigest,
                Trust = evaluated
            };
            var result = await sources.ImportRegistryAsync(retrieved.Content, provenance, scopeRef, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.SourceRegistrySourceImported,
                ActorPrincipalId: actor), cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is SourceRegistryOperationException
                                          or SourceValidationException
                                          or SourceVersionConflictException)
        {
            await audit.WriteAsync(new(SecurityAuditActions.SourceRegistrySourceImported,
                SecurityAuditOutcome.Failed, ActorPrincipalId: actor,
                ReasonCode: exception is SourceRegistryOperationException operation
                    ? operation.Code[..Math.Min(operation.Code.Length, 64)]
                    : "source_registry_import_rejected"), cancellationToken);
            throw;
        }
    }

    private async Task<SelectedCandidate> ResolveSelectionAsync(
        SourceRegistryObservationSelection selection,
        CancellationToken cancellationToken)
    {
        var registration = (await store.ListExactAsync<SourceRegistryRegistrationResource>(
                ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRegistration, 0, 1000, cancellationToken))
            .SingleOrDefault(value => value.Value.Uid == selection.RegistrationUid)?.Value
            ?? throw Failure("source_registry_selection_missing", "The selected registry registration no longer exists.");
        var observation = await FindObservationAsync(registration.Uid, selection.ObservationId, cancellationToken)
            ?? throw Failure("source_registry_selection_missing", "The selected registry observation is no longer retained.");
        SourceRegistryCachedPublication publication;
        try
        {
            publication = await cache.GetAsync(observation.Id, cancellationToken)
                ?? throw Failure("source_registry_selection_evicted", "The selected registry observation was evicted from the cache.");
        }
        catch (IOException exception)
        {
            throw new SourceRegistryOperationException("source_registry_selection_unavailable",
                "The selected registry observation cannot be read from the cache.", true, exception);
        }
        var parsedIndex = ValidateIndex(observation, publication);
        var catalog = observation.Catalogs.SingleOrDefault(value =>
            string.Equals(value.Name, selection.CatalogName, StringComparison.Ordinal))
            ?? throw Failure("source_registry_selection_missing", "The selected registry shard is not present in the observation.");
        var indexCatalog = parsedIndex.Manifest.Definition.Catalogs.SingleOrDefault(value =>
            string.Equals(value.Name, catalog.Name, StringComparison.Ordinal));
        if (indexCatalog is null
            || !string.Equals(indexCatalog.RegistryUrl, catalog.RegistryUrl, StringComparison.Ordinal)
            || !string.Equals(indexCatalog.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal))
            throw Failure("source_registry_selection_index_mismatch", "The selected shard does not match the retained canonical index.");
        if (!publication.Catalogs.TryGetValue(catalog.CachePath, out var bytes))
            throw Failure("source_registry_selection_evicted", "The selected registry shard is missing from the cache.");
        var parsed = registryReader.Read(Encoding.UTF8.GetString(bytes), catalog.CachePath);
        if (!string.Equals(parsed.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal))
            throw Failure("source_registry_selection_digest_mismatch", "The retained registry shard no longer matches its canonical digest.");
        var source = parsed.Manifest.Definition.Sources.SingleOrDefault(value =>
            string.Equals(value.Publisher, selection.Publisher, StringComparison.Ordinal)
            && string.Equals(value.Name, selection.SourceName, StringComparison.Ordinal))
            ?? throw Failure("source_registry_selection_missing", "The selected Source is not present in the retained shard.");
        var version = source.Versions.SingleOrDefault(value =>
            string.Equals(value.Version, selection.Version, StringComparison.Ordinal))
            ?? throw Failure("source_registry_selection_missing", "The selected Source version is not present in the retained shard.");
        var publisher = parsed.Manifest.Definition.Publishers.Single(value =>
            string.Equals(value.Name, source.Publisher, StringComparison.Ordinal));
        var current = await LoadCurrentAsync(cancellationToken);
        var conflicted = current.Where(value => value.Source.Publisher == selection.Publisher
                                               && value.Source.Name == selection.SourceName
                                               && value.Version.Version == selection.Version)
            .Select(value => value.Version.ManifestDigest).Distinct(StringComparer.Ordinal).Skip(1).Any();
        return new(registration, observation, catalog, publisher, source, version, conflicted);
    }

    private async Task<SourceRegistryObservation?> FindObservationAsync(
        Guid registrationUid,
        Guid observationId,
        CancellationToken cancellationToken)
    {
        var states = await store.ListExactAsync<SourceRegistryObservedStateResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryObservedState, 0, 1000, cancellationToken);
        var current = states.SingleOrDefault(value => value.Value.Definition.RegistrationUid == registrationUid)
            ?.Value.Definition.Current;
        if (current?.Id == observationId) return current;
        var records = await store.ListExactAsync<SourceRegistryRefreshRecordResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRefreshRecord, 0, int.MaxValue, cancellationToken);
        return records.Where(value => value.Value.Definition.RegistrationUid == registrationUid)
            .Select(value => value.Value.Definition.Observation)
            .FirstOrDefault(value => value?.Id == observationId);
    }

    private async Task<IReadOnlyList<Candidate>> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var registrations = await store.ListExactAsync<SourceRegistryRegistrationResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRegistration, 0, 1000, cancellationToken);
        var states = await store.ListExactAsync<SourceRegistryObservedStateResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryObservedState, 0, 1000, cancellationToken);
        var result = new List<Candidate>();
        foreach (var stored in registrations.OrderBy(value => value.Value.Name, StringComparer.Ordinal))
        {
            var registration = stored.Value;
            if (!registration.Definition.Enabled) continue;
            var state = states.SingleOrDefault(value => value.Value.Definition.RegistrationUid == registration.Uid)?.Value;
            var observation = state?.Definition.Current;
            if (observation is null) continue;
            SourceRegistryCachedPublication? publication;
            try
            {
                publication = await cache.GetAsync(observation.Id, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            if (publication is null) continue;
            ParsedSourceRegistryIndex parsedIndex;
            try
            {
                parsedIndex = ValidateIndex(observation, publication);
            }
            catch (SourceRegistryOperationException)
            {
                continue;
            }
            catch (SourceValidationException)
            {
                continue;
            }
            foreach (var catalog in observation.Catalogs.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                var indexCatalog = parsedIndex.Manifest.Definition.Catalogs.SingleOrDefault(value =>
                    string.Equals(value.Name, catalog.Name, StringComparison.Ordinal));
                if (indexCatalog is null
                    || !string.Equals(indexCatalog.RegistryUrl, catalog.RegistryUrl, StringComparison.Ordinal)
                    || !string.Equals(indexCatalog.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal)) continue;
                if (!publication.Catalogs.TryGetValue(catalog.CachePath, out var bytes)) continue;
                var parsed = registryReader.Read(Encoding.UTF8.GetString(bytes), catalog.CachePath);
                if (!string.Equals(parsed.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal)) continue;
                foreach (var source in parsed.Manifest.Definition.Sources)
                {
                    var publisher = parsed.Manifest.Definition.Publishers.Single(value => value.Name == source.Publisher);
                    foreach (var version in source.Versions)
                        result.Add(new(registration, state!, observation, catalog, publisher, source, version));
                }
            }
        }
        return result;
    }

    private SourceRegistryDiscoveryObservation ToObservation(Candidate value) => new()
    {
        Selection = new(value.Registration.Uid, value.Observation.Id, value.Catalog.Name,
            value.Source.Publisher, value.Source.Name, value.Version.Version),
        RegistrationName = value.Registration.Name,
        RegistrationDisplayName = value.Registration.Definition.DisplayName,
        TrustPolicy = value.Registration.Definition.TrustPolicy,
        Freshness = Freshness(value),
        OriginClassification = SourceRegistryTrustEvaluationService.ClassifyOrigin(
            value.Registration, value.Observation.FinalIndexUrl),
        IndexDigest = value.Observation.IndexDigest,
        CatalogDigest = value.Catalog.RegistryDigest,
        Compatibility = value.Catalog.Compatibility,
        FetchedAt = value.Observation.FetchedAt,
        Publisher = value.Publisher,
        ManifestUrl = references.ResolveManifestUrl(value.Observation.FinalIndexUrl,
            value.Version.ManifestUrl).AbsoluteUri,
        ManifestDigest = value.Version.ManifestDigest,
        IsCatalogLatest = string.Equals(value.Source.Latest, value.Version.Version, StringComparison.Ordinal)
    };

    private bool MatchesText(Candidate value, SourceRegistryDiscoveryQuery query)
    {
        if (query.Publisher is { Length: > 0 } publisher
            && !string.Equals(value.Source.Publisher, publisher, StringComparison.Ordinal)) return false;
        if (query.Search is not { Length: > 0 } search) return true;
        return value.Source.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || value.Source.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase)
            || value.Source.DisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || value.Source.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool MatchesObservation(Candidate value, SourceRegistryDiscoveryQuery query)
    {
        if (query.Registry is { Length: > 0 } registry
            && !string.Equals(value.Registration.Name, registry, StringComparison.Ordinal)) return false;
        if (query.TrustPolicy is { } policy && value.Registration.Definition.TrustPolicy != policy) return false;
        if (query.FreshOnly && Freshness(value) is not (SourceRegistryObservedStatus.Fresh or SourceRegistryObservedStatus.Recovered)) return false;
        return !query.CompatibleOnly || IsCompatible(value.Catalog.Compatibility.Agentstration);
    }

    private SourceRegistryObservedStatus Freshness(Candidate value)
    {
        if (value.State.Definition.LastSuccessfulRefreshAt is { } successful
            && timeProvider.GetUtcNow() - successful > value.Registration.Definition.RefreshPolicy.StaleAfter)
            return SourceRegistryObservedStatus.Stale;
        return value.State.Definition.Status;
    }

    private bool IsCompatible(SourceCompatibilityBounds? bounds)
    {
        if (bounds is null || !SourceSemanticVersion.TryParse(versions.CurrentVersion, out var running)
            || !SourceSemanticVersion.TryParse(bounds.MinVersion, out var minimum)) return false;
        if (running.CompareTo(minimum) < 0) return false;
        return bounds.MaxVersionExclusive is null
            || SourceSemanticVersion.TryParse(bounds.MaxVersionExclusive, out var maximum)
            && running.CompareTo(maximum) < 0;
    }

    private static SourceRegistryRetrievalContext RetrievalContext(SourceRegistryRegistrationResource registration) =>
        new(ResourceScopeRef.Instance, registration.Address, registration.Definition.EndpointPolicy,
            registration.Definition.AuthenticationMode, registration.Definition.Credential);

    private ParsedSourceRegistryIndex ValidateIndex(
        SourceRegistryObservation observation,
        SourceRegistryCachedPublication publication)
    {
        var parsed = indexReader.Read(Encoding.UTF8.GetString(publication.Index), "index.json",
            PublicationBase(observation.FinalIndexUrl));
        if (!string.Equals(parsed.IndexDigest, observation.IndexDigest, StringComparison.Ordinal))
            throw Failure("source_registry_selection_index_digest_mismatch", "The retained registry index no longer matches its canonical digest.");
        return parsed;
    }

    private static Uri PublicationBase(Uri indexUrl)
    {
        var builder = new UriBuilder(indexUrl) { Query = string.Empty, Fragment = string.Empty };
        var slash = builder.Path.LastIndexOf('/');
        builder.Path = slash < 0 ? "/" : builder.Path[..(slash + 1)];
        return builder.Uri;
    }

    private static SourceRegistryPublisherStatus ParseStatus(string value) => value switch
    {
        SourceRegistryPublisherStatuses.Declared => SourceRegistryPublisherStatus.Declared,
        SourceRegistryPublisherStatuses.Verified => SourceRegistryPublisherStatus.Verified,
        SourceRegistryPublisherStatuses.Official => SourceRegistryPublisherStatus.Official,
        SourceRegistryPublisherStatuses.Revoked => SourceRegistryPublisherStatus.Revoked,
        _ => throw Failure("source_registry_publisher_status_invalid", "The registry publisher status is unsupported.")
    };

    private static void ValidateQuery(SourceRegistryDiscoveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Skip < 0) throw new ArgumentOutOfRangeException(nameof(query), "Skip cannot be negative.");
        if (query.Take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query), "Take must be between 1 and 100.");
        if (query.Search?.Length > 200) throw new ArgumentException("Search cannot exceed 200 characters.", nameof(query));
    }

    private static SourceRegistryOperationException Failure(string code, string message, bool unavailable = false) =>
        new(code, message, unavailable);

    private sealed record Candidate(
        SourceRegistryRegistrationResource Registration,
        SourceRegistryObservedStateResource State,
        SourceRegistryObservation Observation,
        SourceRegistryCatalogObservation Catalog,
        SourceRegistryPublisher Publisher,
        SourceRegistrySource Source,
        SourceRegistryVersion Version);

    private sealed record SelectedCandidate(
        SourceRegistryRegistrationResource Registration,
        SourceRegistryObservation Observation,
        SourceRegistryCatalogObservation Catalog,
        SourceRegistryPublisher Publisher,
        SourceRegistrySource Source,
        SourceRegistryVersion Version,
        bool Conflicted);
}
