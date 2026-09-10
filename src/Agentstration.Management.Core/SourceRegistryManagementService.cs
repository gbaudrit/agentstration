using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceRegistryNotFoundException(string name)
    : Exception($"Source registry registration '{name}' was not found.");

public sealed class SourceRegistryOperationException(string code, string message, bool unavailable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Unavailable { get; } = unavailable;
}

public sealed class SourceRegistryManagementService(
    IControlPlaneStore store,
    ISourceRegistryIndexReader indexReader,
    ISourceRegistryReader registryReader,
    ISourceRegistryReferenceResolver references,
    ISourceRegistryDocumentRetriever documents,
    ISourceRegistryCacheStore cache,
    IAgentstrationVersionProvider versions,
    ResourceScopeOperationService scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public async Task<SourceRegistryRegistrationView> EnsureOfficialAsync(CancellationToken cancellationToken)
    {
        using var system = requestScopes.PushSystem();
        var registration = await GetRegistrationStoredAsync(SourceRegistryWellKnown.OfficialName, cancellationToken);
        if (registration is null)
        {
            try
            {
                registration = await store.PutExactAsync(ResourceScopeRef.Instance, new SourceRegistryRegistrationResource
                {
                    ApiVersion = ManagementApiVersions.CoreV1,
                    Kind = ResourceKinds.SourceRegistryRegistration,
                    Metadata = new ResourceMetadata
                    {
                        Name = SourceRegistryWellKnown.OfficialName,
                        Annotations = new Dictionary<string, string>
                        {
                            [ResourceProvenanceAnnotations.BuiltIn] = "true"
                        }
                    },
                    ScopeRef = ResourceScopeRef.Instance,
                    Generation = 1,
                    Definition = new SourceRegistryRegistrationProperties
                    {
                        DisplayName = SourceRegistryWellKnown.OfficialDisplayName,
                        IndexUrl = new Uri(SourceRegistryWellKnown.OfficialIndexUrl),
                        Enabled = true
                    },
                    Status = SucceededStatus()
                }, null, true, cancellationToken);
            }
            catch (ControlPlaneConcurrencyException)
            {
                registration = await GetRegistrationStoredAsync(SourceRegistryWellKnown.OfficialName, cancellationToken);
                if (registration is null)
                {
                    throw;
                }
            }
        }

        var observed = await EnsureObservedAsync(registration.Value, cancellationToken);
        return new(registration.Value, observed.Value);
    }

    public async Task<IReadOnlyList<SourceRegistryRegistrationView>> ListAsync(CancellationToken cancellationToken)
    {
        var registrations = await store.ListExactAsync<SourceRegistryRegistrationResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRegistration, 0, 1000, cancellationToken);
        var result = new List<SourceRegistryRegistrationView>(registrations.Count);
        foreach (var registration in registrations.OrderBy(value => value.Value.Name, StringComparer.Ordinal))
        {
            var observed = await EnsureObservedAsync(registration.Value, cancellationToken);
            result.Add(new(registration.Value, EffectiveObserved(registration.Value, observed.Value)));
        }
        return result;
    }

    public async Task<SourceRegistryRegistrationView?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var registration = await GetRegistrationStoredAsync(name, cancellationToken);
        if (registration is null) return null;
        var observed = await EnsureObservedAsync(registration.Value, cancellationToken);
        return new(registration.Value, EffectiveObserved(registration.Value, observed.Value));
    }

    public async Task<StoredResource<SourceRegistryRegistrationResource>> UpdateOfficialAsync(
        Uri indexUrl,
        bool enabled,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        ValidateIndexUrl(indexUrl);
        var actor = ActorPrincipalId();
        var existing = await GetRegistrationStoredAsync(SourceRegistryWellKnown.OfficialName, cancellationToken)
            ?? throw new SourceRegistryNotFoundException(SourceRegistryWellKnown.OfficialName);
        try
        {
            var updated = await scopeOperations.WriteAsync(existing.Value, ResourceScopeRef.Instance, AuthorizationPermissions.ResourcesWrite, async token =>
            {
                var stored = await store.PutExactAsync(ResourceScopeRef.Instance, existing.Value with
                {
                    Generation = checked(existing.Value.Generation + 1),
                    Definition = existing.Value.Definition with { IndexUrl = indexUrl, Enabled = enabled },
                    Status = SucceededStatus()
                }, ifMatch, false, token);
                var observed = await EnsureObservedAsync(stored.Value, token);
                if (observed.Value.Definition.Status != EffectiveStatus(stored.Value, observed.Value.Definition))
                {
                    _ = await store.PutExactAsync(ResourceScopeRef.Instance, observed.Value with
                    {
                        Generation = checked(observed.Value.Generation + 1),
                        Definition = observed.Value.Definition with { Status = EffectiveStatus(stored.Value, observed.Value.Definition) }
                    }, observed.ETag, false, token);
                }
                return stored;
            }, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.SourceRegistryConfigurationUpdated, ActorPrincipalId: actor), cancellationToken);
            return updated;
        }
        catch
        {
            await audit.WriteAsync(new(
                SecurityAuditActions.SourceRegistryConfigurationUpdated,
                SecurityAuditOutcome.Failed,
                ActorPrincipalId: actor,
                ReasonCode: "source_registry_update_failed"), cancellationToken);
            throw;
        }
    }

    public async Task<SourceRegistryRegistrationView> RefreshOfficialAsync(CancellationToken cancellationToken)
    {
        var actor = ActorPrincipalId();
        return await scopeOperations.WriteAsync(
            ResourceKinds.SourceRegistryRegistration,
            ResourceScopeRef.Instance,
            AuthorizationPermissions.ResourcesWrite,
            token => RefreshCoreAsync(SourceRegistryWellKnown.OfficialName, actor, token),
            cancellationToken);
    }

    public async Task<IReadOnlyList<SourceRegistryRefreshRecordResource>> ListRefreshesAsync(
        string name,
        int take,
        CancellationToken cancellationToken)
    {
        var registration = await GetRegistrationStoredAsync(name, cancellationToken)
            ?? throw new SourceRegistryNotFoundException(name);
        return (await store.ListExactAsync<SourceRegistryRefreshRecordResource>(
                ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRefreshRecord, 0, 1000, cancellationToken))
            .Where(value => value.Value.Definition.RegistrationUid == registration.Value.Uid)
            .OrderByDescending(value => value.Value.Definition.CompletedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(value => value.Value)
            .ToArray();
    }

    private async Task<SourceRegistryRegistrationView> RefreshCoreAsync(
        string name,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var registration = await GetRegistrationStoredAsync(name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(name);
            var observed = await EnsureObservedAsync(registration.Value, cancellationToken);
            var attemptedAt = timeProvider.GetUtcNow();
            if (!registration.Value.Definition.Enabled)
            {
                var disabled = new SourceRegistryOperationException("source_registry_disabled", "The official Source registry is disabled.");
                await RecordFailureAsync(registration.Value, observed, attemptedAt, SourceRegistryRefreshOutcome.Disabled, disabled, actor, cancellationToken);
                throw disabled;
            }

            try
            {
                var current = observed.Value.Definition.Current;
                var retrievedIndex = await documents.RetrieveAsync(
                    registration.Value.Definition.IndexUrl,
                    current?.ETag,
                    current?.LastModified,
                    SourceRegistryLimits.MaximumIndexDocumentBytes,
                    cancellationToken);
                if (retrievedIndex.NotModified)
                {
                    if (current is null)
                        throw new SourceRegistryOperationException("source_registry_not_modified_without_cache", "The registry returned HTTP 304 before a valid observation was cached.");
                    var unchanged = await SaveSuccessAsync(
                        registration,
                        observed,
                        attemptedAt,
                        SourceRegistryRefreshOutcome.NotModified,
                        current,
                        actor,
                        cancellationToken);
                    return new(registration.Value, unchanged.Value);
                }

                var baseUri = PublicationBase(retrievedIndex.FinalUrl);
                var parsedIndex = indexReader.Read(
                    retrievedIndex.Content ?? throw new SourceRegistryOperationException("source_registry_index_empty", "The registry index response was empty."),
                    retrievedIndex.FileName,
                    baseUri);
                var runningVersionText = versions.CurrentVersion;
                if (!SourceSemanticVersion.TryParse(runningVersionText, out var runningVersion))
                    throw new SourceRegistryOperationException("agentstration_version_unavailable", "The running Agentstration version is not a valid Semantic Version.");
                var selected = parsedIndex.Manifest.Definition.Catalogs
                    .Where(value => Contains(value.Compatibility.Agentstration!, runningVersion))
                    .ToArray();
                if (selected.Length == 0)
                    throw new SourceRegistryOperationException(
                        "no_compatible_registry_catalog",
                        $"The registry has no catalogue compatible with Agentstration {runningVersionText}.");

                var cachedCatalogs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var observations = new List<SourceRegistryCatalogObservation>(selected.Length);
                foreach (var catalog in selected)
                {
                    var publicationPath = references.ResolveRegistryPublicationPath(baseUri, catalog.RegistryUrl);
                    var shardUrl = new Uri(baseUri, publicationPath);
                    var retrievedShard = await documents.RetrieveAsync(
                        shardUrl,
                        null,
                        null,
                        SourceRegistryLimits.MaximumDocumentBytes,
                        cancellationToken);
                    if (retrievedShard.NotModified || retrievedShard.Content is null)
                        throw new SourceRegistryOperationException("source_registry_catalog_empty", $"Registry catalogue '{catalog.Name}' returned no content.");
                    var parsedShard = registryReader.Read(retrievedShard.Content, retrievedShard.FileName);
                    if (!string.Equals(parsedShard.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal))
                        throw new SourceRegistryOperationException(
                            "source_registry_catalog_digest_mismatch",
                            $"Registry catalogue '{catalog.Name}' does not match its declared canonical digest.");
                    cachedCatalogs.Add(publicationPath, parsedShard.CanonicalJson);
                    observations.Add(new SourceRegistryCatalogObservation
                    {
                        Name = catalog.Name,
                        Compatibility = catalog.Compatibility,
                        RegistryUrl = catalog.RegistryUrl,
                        RegistryDigest = parsedShard.RegistryDigest,
                        CachePath = publicationPath
                    });
                }

                var observation = new SourceRegistryObservation
                {
                    Id = Guid.NewGuid(),
                    RequestedIndexUrl = registration.Value.Definition.IndexUrl,
                    FinalIndexUrl = retrievedIndex.FinalUrl,
                    IndexDigest = parsedIndex.IndexDigest,
                    ETag = retrievedIndex.ETag,
                    LastModified = retrievedIndex.LastModified,
                    FetchedAt = timeProvider.GetUtcNow(),
                    AgentstrationVersion = runningVersionText!,
                    Catalogs = observations
                };
                await cache.StoreAsync(new(observation.Id, parsedIndex.CanonicalJson, cachedCatalogs), cancellationToken);

                var latest = await GetRegistrationStoredAsync(name, cancellationToken)
                    ?? throw new SourceRegistryNotFoundException(name);
                if (!string.Equals(latest.ETag, registration.ETag, StringComparison.Ordinal)
                    || !latest.Value.Definition.Enabled
                    || latest.Value.Definition.IndexUrl != registration.Value.Definition.IndexUrl)
                    throw new SourceRegistryOperationException("source_registry_configuration_changed", "The registry configuration changed during refresh; the new observation was not activated.");

                var storedObserved = await SaveSuccessAsync(
                    registration,
                    observed,
                    attemptedAt,
                    SourceRegistryRefreshOutcome.Succeeded,
                    observation,
                    actor,
                    cancellationToken);
                return new(registration.Value, storedObserved.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedRefreshFailure(exception))
            {
                var operation = Normalize(exception);
                var outcome = Outcome(operation);
                await RecordFailureAsync(registration.Value, observed, attemptedAt, outcome, operation, actor, cancellationToken);
                throw operation;
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<StoredResource<SourceRegistryObservedStateResource>> SaveSuccessAsync(
        StoredResource<SourceRegistryRegistrationResource> registration,
        StoredResource<SourceRegistryObservedStateResource> observed,
        DateTimeOffset attemptedAt,
        SourceRegistryRefreshOutcome outcome,
        SourceRegistryObservation observation,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        var completedAt = timeProvider.GetUtcNow();
        var updated = await store.PutExactAsync(ResourceScopeRef.Instance, observed.Value with
        {
            Generation = checked(observed.Value.Generation + 1),
            Definition = observed.Value.Definition with
            {
                Status = SourceRegistryObservedStatus.Fresh,
                LastAttemptedAt = attemptedAt,
                LastSuccessfulRefreshAt = completedAt,
                LastOutcome = outcome,
                LastErrorCode = null,
                LastErrorMessage = null,
                Current = observation
            },
            Status = SucceededStatus()
        }, observed.ETag, false, cancellationToken);
        await RecordRefreshAsync(registration.Value, attemptedAt, completedAt, outcome, null, observation, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.SourceRegistryRefreshed, ActorPrincipalId: actor), cancellationToken);
        return updated;
    }

    private async Task RecordFailureAsync(
        SourceRegistryRegistrationResource registration,
        StoredResource<SourceRegistryObservedStateResource> observed,
        DateTimeOffset attemptedAt,
        SourceRegistryRefreshOutcome outcome,
        SourceRegistryOperationException exception,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        var completedAt = timeProvider.GetUtcNow();
        var status = observed.Value.Definition.Current is not null
            ? SourceRegistryObservedStatus.Stale
            : outcome switch
            {
                SourceRegistryRefreshOutcome.Invalid => SourceRegistryObservedStatus.Invalid,
                SourceRegistryRefreshOutcome.NoCompatibleCatalog => SourceRegistryObservedStatus.NoCompatibleCatalog,
                SourceRegistryRefreshOutcome.Disabled => SourceRegistryObservedStatus.Disabled,
                _ => SourceRegistryObservedStatus.RefreshFailed
            };
        _ = await store.PutExactAsync(ResourceScopeRef.Instance, observed.Value with
        {
            Generation = checked(observed.Value.Generation + 1),
            Definition = observed.Value.Definition with
            {
                Status = status,
                LastAttemptedAt = attemptedAt,
                LastOutcome = outcome,
                LastErrorCode = exception.Code,
                LastErrorMessage = exception.Message
            },
            Status = new ResourceStatus
            {
                ProvisioningState = ProvisioningState.Failed,
                Conditions = [new ResourceCondition
                {
                    Type = "RefreshReady",
                    Status = "False",
                    Reason = exception.Code,
                    Message = exception.Message,
                    LastTransitionTime = completedAt
                }]
            }
        }, observed.ETag, false, cancellationToken);
        await RecordRefreshAsync(registration, attemptedAt, completedAt, outcome, exception, observed.Value.Definition.Current, cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.SourceRegistryRefreshed,
            SecurityAuditOutcome.Failed,
            ActorPrincipalId: actor,
            ReasonCode: exception.Code.Length <= 64 ? exception.Code : "source_registry_refresh_failed"), cancellationToken);
    }

    private Task RecordRefreshAsync(
        SourceRegistryRegistrationResource registration,
        DateTimeOffset attemptedAt,
        DateTimeOffset completedAt,
        SourceRegistryRefreshOutcome outcome,
        SourceRegistryOperationException? exception,
        SourceRegistryObservation? observation,
        CancellationToken cancellationToken) =>
        store.CreateImmutableAsync(new SourceRegistryRefreshRecordResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceRegistryRefreshRecord,
            Metadata = new ResourceMetadata { Name = $"{registration.Uid:N}-{Guid.NewGuid():N}" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new SourceRegistryRefreshRecordProperties
            {
                RegistrationUid = registration.Uid,
                AttemptedAt = attemptedAt,
                CompletedAt = completedAt,
                Outcome = outcome,
                ErrorCode = exception?.Code,
                ErrorMessage = exception?.Message,
                ObservationId = observation?.Id,
                IndexDigest = observation?.IndexDigest
            }
        }, cancellationToken);

    private async Task<StoredResource<SourceRegistryObservedStateResource>> EnsureObservedAsync(
        SourceRegistryRegistrationResource registration,
        CancellationToken cancellationToken)
    {
        var existing = await GetObservedStoredAsync(registration.Name, cancellationToken);
        if (existing is not null) return existing;
        try
        {
            return await store.PutExactAsync(ResourceScopeRef.Instance, new SourceRegistryObservedStateResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.SourceRegistryObservedState,
                Metadata = new ResourceMetadata { Name = registration.Name },
                ScopeRef = ResourceScopeRef.Instance,
                Generation = 1,
                Definition = new SourceRegistryObservedStateProperties
                {
                    RegistrationUid = registration.Uid,
                    Status = registration.Definition.Enabled
                        ? SourceRegistryObservedStatus.NeverFetched
                        : SourceRegistryObservedStatus.Disabled
                },
                Status = SucceededStatus()
            }, null, true, cancellationToken);
        }
        catch (ControlPlaneConcurrencyException)
        {
            var concurrent = await GetObservedStoredAsync(registration.Name, cancellationToken);
            if (concurrent is null)
            {
                throw;
            }

            return concurrent;
        }
    }

    private Task<StoredResource<SourceRegistryRegistrationResource>?> GetRegistrationStoredAsync(
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<SourceRegistryRegistrationResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ResourceKinds.SourceRegistryRegistration, name),
            cancellationToken);

    private Task<StoredResource<SourceRegistryObservedStateResource>?> GetObservedStoredAsync(
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<SourceRegistryObservedStateResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ResourceKinds.SourceRegistryObservedState, name),
            cancellationToken);

    private static SourceRegistryObservedStateResource EffectiveObserved(
        SourceRegistryRegistrationResource registration,
        SourceRegistryObservedStateResource observed) =>
        observed.Definition.Status == EffectiveStatus(registration, observed.Definition)
            ? observed
            : observed with { Definition = observed.Definition with { Status = EffectiveStatus(registration, observed.Definition) } };

    private static SourceRegistryObservedStatus EffectiveStatus(
        SourceRegistryRegistrationResource registration,
        SourceRegistryObservedStateProperties observed) =>
        registration.Definition.Enabled
            ? observed.Status == SourceRegistryObservedStatus.Disabled
                ? observed.Current is null ? SourceRegistryObservedStatus.NeverFetched : SourceRegistryObservedStatus.Fresh
                : observed.Status
            : SourceRegistryObservedStatus.Disabled;

    private static bool Contains(SourceCompatibilityBounds bounds, SourceSemanticVersion version)
    {
        _ = SourceSemanticVersion.TryParse(bounds.MinVersion, out var minimum);
        if (version.CompareTo(minimum) < 0) return false;
        return bounds.MaxVersionExclusive is null
            || SourceSemanticVersion.TryParse(bounds.MaxVersionExclusive, out var maximum) && version.CompareTo(maximum) < 0;
    }

    private static Uri PublicationBase(Uri indexUrl)
    {
        var fileName = Path.GetFileName(indexUrl.AbsolutePath);
        if (!fileName.Equals("index.json", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("index.yaml", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("index.yml", StringComparison.OrdinalIgnoreCase))
            throw new SourceRegistryOperationException("source_registry_index_url_invalid", "The final registry index URL must end in index.json, index.yaml, or index.yml.");
        return new Uri(indexUrl, ".");
    }

    private static void ValidateIndexUrl(Uri indexUrl)
    {
        ArgumentNullException.ThrowIfNull(indexUrl);
        if (!indexUrl.IsAbsoluteUri || indexUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(indexUrl.UserInfo) || !string.IsNullOrEmpty(indexUrl.Query)
            || !string.IsNullOrEmpty(indexUrl.Fragment))
            throw new SourceRegistryOperationException("source_registry_index_url_invalid", "The registry index URL must be absolute HTTPS without credentials, query, or fragment.");
        _ = PublicationBase(indexUrl);
    }

    private static bool IsExpectedRefreshFailure(Exception exception) => exception is
        SourceRegistryOperationException or SourceValidationException or SourceRetrievalException or IOException or HttpRequestException;

    private static SourceRegistryOperationException Normalize(Exception exception) => exception switch
    {
        SourceRegistryOperationException operation => operation,
        SourceValidationException validation => new(validation.Code, validation.Message, innerException: validation),
        SourceRetrievalException retrieval => new(retrieval.Code, retrieval.Message, unavailable: true, innerException: retrieval),
        IOException io => new("source_registry_cache_unavailable", "The registry cache could not be updated.", unavailable: true, innerException: io),
        HttpRequestException http => new("source_registry_unavailable", "The registry could not be reached.", unavailable: true, innerException: http),
        _ => throw new ArgumentOutOfRangeException(nameof(exception))
    };

    private static SourceRegistryRefreshOutcome Outcome(SourceRegistryOperationException exception) =>
        exception.Code == "no_compatible_registry_catalog"
            ? SourceRegistryRefreshOutcome.NoCompatibleCatalog
            : exception.Unavailable
                ? SourceRegistryRefreshOutcome.Unavailable
                : SourceRegistryRefreshOutcome.Invalid;

    private Guid? ActorPrincipalId() => requestContext.IsInitialized ? requestContext.Current.PrincipalId : null;

    private static ResourceStatus SucceededStatus() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
