using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Microsoft.Extensions.Logging;

namespace Agentstration.Management.Core;

public sealed class SourceRegistryNotFoundException(string name)
    : Exception($"Source registry registration '{name}' was not found.");

public sealed class SourceRegistryOperationException(string code, string message, bool unavailable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Unavailable { get; } = unavailable;
}

public sealed partial class SourceRegistryManagementService(
    IResourceStore store,
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
    TimeProvider timeProvider,
    ILogger<SourceRegistryManagementService> logger)
{
    public static readonly Meter Meter = new("Agentstration.SourceRegistry");
    private static readonly Counter<long> RefreshCounter = Meter.CreateCounter<long>("agentstration.source_registry.refreshes");
    private static readonly Histogram<double> RefreshDuration = Meter.CreateHistogram<double>("agentstration.source_registry.refresh.duration", "s");
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
                        Enabled = true,
                        TrustPolicy = SourceRegistryTrustPolicy.Authoritative
                    },
                    Status = SucceededStatus()
                }, null, true, cancellationToken);
            }
            catch (ResourceConcurrencyException)
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
        var existing = await GetRegistrationStoredAsync(SourceRegistryWellKnown.OfficialName, cancellationToken)
            ?? throw new SourceRegistryNotFoundException(SourceRegistryWellKnown.OfficialName);
        return await UpdateAsync(
            SourceRegistryWellKnown.OfficialName,
            existing.Value.Definition with { IndexUrl = indexUrl, Enabled = enabled },
            ifMatch,
            cancellationToken);
    }

    public async Task<StoredResource<SourceRegistryRegistrationResource>> CreateAsync(
        string name,
        SourceRegistryRegistrationProperties definition,
        CancellationToken cancellationToken)
    {
        var actor = ActorPrincipalId();
        try
        {
            await ValidateDefinitionAsync(name, definition, cancellationToken);
            var created = await scopeOperations.WriteAsync(
                ResourceKinds.SourceRegistryRegistration,
                ResourceScopeRef.Instance,
                AuthorizationPermissions.ResourcesWrite,
                async token =>
                {
                    var stored = await store.PutExactAsync(ResourceScopeRef.Instance, new SourceRegistryRegistrationResource
                    {
                        ApiVersion = ManagementApiVersions.CoreV1,
                        Kind = ResourceKinds.SourceRegistryRegistration,
                        Metadata = new ResourceMetadata { Name = name },
                        ScopeRef = ResourceScopeRef.Instance,
                        Generation = 1,
                        Definition = definition with { DisplayName = definition.DisplayName.Trim() },
                        Status = SucceededStatus()
                    }, null, true, token);
                    _ = await EnsureObservedAsync(stored.Value, token);
                    return stored;
                },
                cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.SourceRegistryCreated, ActorPrincipalId: actor), cancellationToken);
            return created;
        }
        catch
        {
            await AuditFailureAsync(SecurityAuditActions.SourceRegistryCreated, "source_registry_create_failed", actor, cancellationToken);
            throw;
        }
    }

    public async Task<StoredResource<SourceRegistryRegistrationResource>> UpdateAsync(
        string name,
        SourceRegistryRegistrationProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        var actor = ActorPrincipalId();
        try
        {
            await ValidateDefinitionAsync(name, definition, cancellationToken);
            var existing = await GetRegistrationStoredAsync(name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(name);
            var updated = await scopeOperations.WriteAsync(existing.Value, ResourceScopeRef.Instance, AuthorizationPermissions.ResourcesWrite, async token =>
            {
                var stored = await store.PutExactAsync(ResourceScopeRef.Instance, existing.Value with
                {
                    Generation = checked(existing.Value.Generation + 1),
                    Definition = definition with { DisplayName = definition.DisplayName.Trim() },
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
            await AuditFailureAsync(SecurityAuditActions.SourceRegistryConfigurationUpdated, "source_registry_update_failed", actor, cancellationToken);
            throw;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task DeleteAsync(string name, string? ifMatch, CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        var actor = ActorPrincipalId();
        try
        {
            if (string.Equals(name, SourceRegistryWellKnown.OfficialName, StringComparison.Ordinal))
                throw new SourceRegistryOperationException("source_registry_official_delete_forbidden", "The official Source registry can be disabled but not deleted.");
            var existing = await GetRegistrationStoredAsync(name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(name);
            await scopeOperations.WriteAsync(existing.Value, ResourceScopeRef.Instance, AuthorizationPermissions.ResourcesDelete, async token =>
            {
                await store.DeleteExactAsync(
                    ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ResourceKinds.SourceRegistryRegistration, name),
                    ifMatch,
                    token);
                return true;
            }, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.SourceRegistryDeleted, ActorPrincipalId: actor), cancellationToken);
        }
        catch
        {
            await AuditFailureAsync(SecurityAuditActions.SourceRegistryDeleted, "source_registry_delete_failed", actor, cancellationToken);
            throw;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<SourceRegistryRegistrationView> RefreshOfficialAsync(CancellationToken cancellationToken)
        => await RefreshAsync(SourceRegistryWellKnown.OfficialName, cancellationToken);

    public async Task<SourceRegistryRegistrationView> RefreshAsync(string name, CancellationToken cancellationToken)
        => await RefreshAsync(name, SourceRegistryRefreshTrigger.Manual, 0, null, cancellationToken);

    public async Task<SourceRegistryRegistrationView> RefreshAsync(
        string name,
        SourceRegistryRefreshTrigger trigger,
        int retryCount,
        DateTimeOffset? expectedLastAttemptedAt,
        CancellationToken cancellationToken)
    {
        var actor = ActorPrincipalId();
        return await scopeOperations.WriteAsync(
            ResourceKinds.SourceRegistryRegistration,
            ResourceScopeRef.Instance,
            AuthorizationPermissions.ResourcesWrite,
            token => RefreshCoreAsync(name, trigger, retryCount, expectedLastAttemptedAt, actor, token),
            cancellationToken);
    }

    public async Task RecordScheduledFailureAsync(
        string name,
        string code,
        string message,
        int retryCount,
        CancellationToken cancellationToken)
    {
        await scopeOperations.WriteAsync(
            ResourceKinds.SourceRegistryRegistration,
            ResourceScopeRef.Instance,
            AuthorizationPermissions.ResourcesWrite,
            async token =>
            {
                await refreshGate.WaitAsync(token);
                try
                {
                    var registration = await GetRegistrationStoredAsync(name, token)
                        ?? throw new SourceRegistryNotFoundException(name);
                    if (!registration.Value.Definition.Enabled) return true;
                    var observed = await EnsureObservedAsync(registration.Value, token);
                    var completedAt = timeProvider.GetUtcNow();
                    var attemptedAt = completedAt - registration.Value.Definition.RefreshPolicy.Timeout;
                    await RecordFailureAsync(registration.Value, observed, attemptedAt,
                        SourceRegistryRefreshOutcome.Unavailable,
                        new SourceRegistryOperationException(code, message, unavailable: true),
                        SourceRegistryRefreshTrigger.Scheduled, retryCount, null, token);
                    return true;
                }
                finally
                {
                    refreshGate.Release();
                }
            },
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
        SourceRegistryRefreshTrigger trigger,
        int retryCount,
        DateTimeOffset? expectedLastAttemptedAt,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(trigger) || retryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(trigger));
        using var operationActivity = Activity.Current is null
            ? new Activity("SourceRegistryRefresh").Start()
            : null;
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var registration = await GetRegistrationStoredAsync(name, cancellationToken)
                ?? throw new SourceRegistryNotFoundException(name);
            var observed = await EnsureObservedAsync(registration.Value, cancellationToken);
            if (trigger == SourceRegistryRefreshTrigger.Scheduled
                && observed.Value.Definition.LastAttemptedAt != expectedLastAttemptedAt)
                return new(registration.Value, EffectiveObserved(registration.Value, observed.Value));
            var attemptedAt = timeProvider.GetUtcNow();
            if (!registration.Value.Definition.Enabled)
            {
                var disabled = new SourceRegistryOperationException("source_registry_disabled", $"Source registry '{name}' is disabled.");
                await RecordFailureAsync(registration.Value, observed, attemptedAt, SourceRegistryRefreshOutcome.Disabled,
                    disabled, trigger, retryCount, actor, cancellationToken);
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
                    RetrievalContext(registration.Value),
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
                        trigger,
                        retryCount,
                        correlationId,
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
                        RetrievalContext(registration.Value),
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
                    trigger,
                    retryCount,
                    correlationId,
                    actor,
                    cancellationToken);
                await CleanupCacheAsync(registration.Value, observation.Id, cancellationToken);
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
                await RecordFailureAsync(registration.Value, observed, attemptedAt, outcome, operation,
                    trigger, retryCount, actor, cancellationToken);
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
        SourceRegistryRefreshTrigger trigger,
        int retryCount,
        string correlationId,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        var completedAt = timeProvider.GetUtcNow();
        var recovered = observed.Value.Definition.ConsecutiveFailures > 0;
        var durationMilliseconds = DurationMilliseconds(attemptedAt, completedAt);
        var updated = await store.PutExactAsync(ResourceScopeRef.Instance, observed.Value with
        {
            Generation = checked(observed.Value.Generation + 1),
            Definition = observed.Value.Definition with
            {
                Status = recovered ? SourceRegistryObservedStatus.Recovered : SourceRegistryObservedStatus.Fresh,
                LastAttemptedAt = attemptedAt,
                LastSuccessfulRefreshAt = completedAt,
                LastOutcome = outcome,
                LastErrorCode = null,
                LastErrorMessage = null,
                Current = observation,
                LastTrigger = trigger,
                ConsecutiveFailures = 0,
                LastRetryCount = retryCount,
                LastDurationMilliseconds = durationMilliseconds,
                LastRecoveredAt = recovered ? completedAt : observed.Value.Definition.LastRecoveredAt
            },
            Status = SucceededStatus()
        }, observed.ETag, false, cancellationToken);
        await RecordRefreshAsync(registration.Value, attemptedAt, completedAt, outcome, null, observation,
            trigger, retryCount, durationMilliseconds, correlationId, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.SourceRegistryRefreshed, ActorPrincipalId: actor), cancellationToken);
        ObserveRefresh(registration.Value.Uid, trigger, outcome, retryCount, durationMilliseconds, correlationId);
        return updated;
    }

    private async Task RecordFailureAsync(
        SourceRegistryRegistrationResource registration,
        StoredResource<SourceRegistryObservedStateResource> observed,
        DateTimeOffset attemptedAt,
        SourceRegistryRefreshOutcome outcome,
        SourceRegistryOperationException exception,
        SourceRegistryRefreshTrigger trigger,
        int retryCount,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        var completedAt = timeProvider.GetUtcNow();
        var durationMilliseconds = DurationMilliseconds(attemptedAt, completedAt);
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var status = observed.Value.Definition.Current is not null
            ? SourceRegistryObservedStatus.Stale
            : outcome switch
            {
                SourceRegistryRefreshOutcome.Invalid => SourceRegistryObservedStatus.Invalid,
                SourceRegistryRefreshOutcome.NoCompatibleCatalog => SourceRegistryObservedStatus.NoCompatibleCatalog,
                SourceRegistryRefreshOutcome.Disabled => SourceRegistryObservedStatus.Disabled,
                SourceRegistryRefreshOutcome.PolicyDenied => SourceRegistryObservedStatus.PolicyDenied,
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
                LastErrorMessage = exception.Message,
                LastTrigger = trigger,
                ConsecutiveFailures = observed.Value.Definition.ConsecutiveFailures == int.MaxValue
                    ? int.MaxValue
                    : observed.Value.Definition.ConsecutiveFailures + 1,
                LastRetryCount = retryCount,
                LastDurationMilliseconds = durationMilliseconds
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
        await RecordRefreshAsync(registration, attemptedAt, completedAt, outcome, exception,
            observed.Value.Definition.Current, trigger, retryCount, durationMilliseconds, correlationId, cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.SourceRegistryRefreshed,
            SecurityAuditOutcome.Failed,
            ActorPrincipalId: actor,
            ReasonCode: exception.Code.Length <= 64 ? exception.Code : "source_registry_refresh_failed"), cancellationToken);
        ObserveRefresh(registration.Uid, trigger, outcome, retryCount, durationMilliseconds, correlationId);
    }

    private Task RecordRefreshAsync(
        SourceRegistryRegistrationResource registration,
        DateTimeOffset attemptedAt,
        DateTimeOffset completedAt,
        SourceRegistryRefreshOutcome outcome,
        SourceRegistryOperationException? exception,
        SourceRegistryObservation? observation,
        SourceRegistryRefreshTrigger trigger,
        int retryCount,
        long durationMilliseconds,
        string correlationId,
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
                IndexDigest = observation?.IndexDigest,
                Trigger = trigger,
                RetryCount = retryCount,
                DurationMilliseconds = durationMilliseconds,
                CorrelationId = correlationId,
                Observation = observation
            }
        }, cancellationToken);

    private async Task CleanupCacheAsync(
        SourceRegistryRegistrationResource registration,
        Guid currentObservationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await store.ListExactAsync<SourceRegistryRefreshRecordResource>(
                ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRefreshRecord, 0, int.MaxValue, cancellationToken);
            var retained = new HashSet<Guid>();
            var candidates = records
                .Where(value => value.Value.Definition.RegistrationUid == registration.Uid)
                .OrderByDescending(value => value.Value.Definition.CompletedAt)
                .Select(value => value.Value.Definition.ObservationId)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .ToArray();
            foreach (var observationId in candidates.Take(registration.Definition.CachePolicy.RetainedObservations))
                retained.Add(observationId);
            retained.Add(currentObservationId);
            foreach (var observationId in candidates.Where(value => !retained.Contains(value)))
                await cache.RemoveAsync(observationId, cancellationToken);
        }
        catch (IOException exception)
        {
            LogCacheCleanupFailed(logger, registration.Uid, exception);
        }
    }

    private void ObserveRefresh(
        Guid registrationUid,
        SourceRegistryRefreshTrigger trigger,
        SourceRegistryRefreshOutcome outcome,
        int retryCount,
        long durationMilliseconds,
        string correlationId)
    {
        var tags = new TagList
        {
            { "registration.uid", registrationUid.ToString("D") },
            { "refresh.trigger", trigger.ToString() },
            { "refresh.result", outcome.ToString() },
            { "refresh.retry_count", retryCount }
        };
        RefreshCounter.Add(1, tags);
        RefreshDuration.Record(durationMilliseconds / 1000d, tags);
        LogRefreshCompleted(logger, registrationUid, trigger, durationMilliseconds, outcome, retryCount, correlationId);
    }

    private static long DurationMilliseconds(DateTimeOffset attemptedAt, DateTimeOffset completedAt) =>
        Math.Max(0, (long)(completedAt - attemptedAt).TotalMilliseconds);

    private async Task<StoredResource<SourceRegistryObservedStateResource>> EnsureObservedAsync(
        SourceRegistryRegistrationResource registration,
        CancellationToken cancellationToken)
    {
        var existing = await GetObservedStoredAsync(registration.Name, cancellationToken);
        if (existing is not null && existing.Value.Definition.RegistrationUid == registration.Uid) return existing;
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
            }, existing?.ETag, existing is null, cancellationToken);
        }
        catch (ResourceConcurrencyException)
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

    private SourceRegistryObservedStateResource EffectiveObserved(
        SourceRegistryRegistrationResource registration,
        SourceRegistryObservedStateResource observed) =>
        observed.Definition.Status == EffectiveStatus(registration, observed.Definition)
            ? observed
            : observed with { Definition = observed.Definition with { Status = EffectiveStatus(registration, observed.Definition) } };

    private SourceRegistryObservedStatus EffectiveStatus(
        SourceRegistryRegistrationResource registration,
        SourceRegistryObservedStateProperties observed)
    {
        if (!registration.Definition.Enabled) return SourceRegistryObservedStatus.Disabled;
        var status = observed.Status == SourceRegistryObservedStatus.Disabled
            ? observed.Current is null ? SourceRegistryObservedStatus.NeverFetched : SourceRegistryObservedStatus.Fresh
            : observed.Status;
        return status is SourceRegistryObservedStatus.Fresh or SourceRegistryObservedStatus.Recovered
            && observed.LastSuccessfulRefreshAt is { } lastSuccessful
            && timeProvider.GetUtcNow() - lastSuccessful >= registration.Definition.RefreshPolicy.StaleAfter
                ? SourceRegistryObservedStatus.Stale
                : status;
    }

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

    private async Task ValidateDefinitionAsync(
        string name,
        SourceRegistryRegistrationProperties definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!RegistryNamePattern().IsMatch(name))
            throw new SourceRegistryOperationException("source_registry_identity_invalid", "The registry name must be a canonical lowercase portable name.");
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || definition.DisplayName.Trim().Length > 256)
            throw new SourceRegistryOperationException("source_registry_display_name_invalid", "The registry display name must contain 1 to 256 characters.");
        if (!Enum.IsDefined(definition.TrustPolicy) || !Enum.IsDefined(definition.AuthenticationMode))
            throw new SourceRegistryOperationException("source_registry_policy_invalid", "The registry policy contains an unsupported value.");
        if (definition.RefreshPolicy.Interval < TimeSpan.FromMinutes(1)
            || definition.RefreshPolicy.Interval > TimeSpan.FromDays(30))
            throw new SourceRegistryOperationException("source_registry_refresh_policy_invalid", "The registry refresh interval must be between one minute and 30 days.");
        if (definition.RefreshPolicy.Timeout < TimeSpan.FromSeconds(1)
            || definition.RefreshPolicy.Timeout > TimeSpan.FromMinutes(5)
            || definition.RefreshPolicy.MaximumAttempts is < 1 or > 10
            || definition.RefreshPolicy.InitialBackoff < TimeSpan.FromSeconds(1)
            || definition.RefreshPolicy.MaximumBackoff < definition.RefreshPolicy.InitialBackoff
            || definition.RefreshPolicy.MaximumBackoff > TimeSpan.FromDays(1)
            || definition.RefreshPolicy.Jitter < TimeSpan.Zero
            || definition.RefreshPolicy.Jitter > TimeSpan.FromHours(1)
            || definition.RefreshPolicy.Jitter > definition.RefreshPolicy.Interval / 2
            || definition.RefreshPolicy.StaleAfter < definition.RefreshPolicy.Interval
            || definition.RefreshPolicy.StaleAfter > TimeSpan.FromDays(90))
            throw new SourceRegistryOperationException("source_registry_refresh_policy_invalid", "The registry refresh timeout, retry, jitter, and staleness settings are outside their supported bounds.");
        if (definition.CachePolicy.RetainedObservations is < 1 or > 100)
            throw new SourceRegistryOperationException("source_registry_cache_policy_invalid", "The registry cache must retain between 1 and 100 observations.");
        ValidateIndexUrl(definition.IndexUrl, definition.EndpointPolicy);
        await ValidateCredentialAsync(definition, cancellationToken);
    }

    private async Task ValidateCredentialAsync(
        SourceRegistryRegistrationProperties definition,
        CancellationToken cancellationToken)
    {
        if (definition.AuthenticationMode == SourceRegistryAuthenticationMode.None && definition.Credential is not null)
            throw new SourceRegistryOperationException("source_registry_credential_invalid", "A registry credential requires staticBearer authentication.");
        if (definition.AuthenticationMode == SourceRegistryAuthenticationMode.StaticBearer && definition.Credential is null)
            throw new SourceRegistryOperationException("source_registry_credential_invalid", "Static Bearer authentication requires an instance Secret reference.");
        if (definition.Credential is not { } credential) return;
        if (credential.ScopeRef is not { } credentialScope || credentialScope != ResourceScopeRef.Instance)
            throw new SourceRegistryOperationException("source_registry_credential_scope_invalid", "The registry credential must explicitly reference an instance-scoped Secret.");
        var secretAddress = credential.Resolve(ResourceNamespace.Default, ResourceKinds.Secret);
        var secret = (await store.GetExactAsync<SecretResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, secretAddress.Namespace, ResourceKinds.Secret, secretAddress.Name),
            cancellationToken))?.Value;
        if (secret is null)
            throw new SourceRegistryOperationException("source_registry_credential_not_found", "The referenced registry credential Secret was not found.");
        var vaultReference = secret.Definition.Vault;
        if (vaultReference.ScopeRef is { } vaultScope && vaultScope != ResourceScopeRef.Instance)
            throw new SourceRegistryOperationException("source_registry_credential_scope_invalid", "The registry credential Vault must be instance-scoped.");
        var vaultAddress = vaultReference.Resolve(secret.Namespace, ResourceKinds.Vault);
        if (await store.GetExactAsync<VaultResource>(
                ScopedResourceAddress.Create(ResourceScopeRef.Instance, vaultAddress.Namespace, ResourceKinds.Vault, vaultAddress.Name),
                cancellationToken) is null)
            throw new SourceRegistryOperationException("source_registry_credential_vault_not_found", "The registry credential Vault was not found in the instance scope.");
    }

    private static void ValidateIndexUrl(Uri indexUrl, SourceRegistryEndpointPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(indexUrl);
        ArgumentNullException.ThrowIfNull(policy);
        var permittedScheme = indexUrl.Scheme == Uri.UriSchemeHttps
            || policy.AllowHttp && indexUrl.Scheme == Uri.UriSchemeHttp;
        if (!indexUrl.IsAbsoluteUri || !permittedScheme
            || !string.IsNullOrEmpty(indexUrl.UserInfo) || !string.IsNullOrEmpty(indexUrl.Query)
            || !string.IsNullOrEmpty(indexUrl.Fragment) || indexUrl.AbsolutePath.Contains('%'))
            throw new SourceRegistryOperationException("source_registry_index_url_invalid", "The registry index URL must use an authorized HTTP(S) origin without credentials, query, fragment, or percent-encoding.");
        if (System.Net.IPAddress.TryParse(indexUrl.IdnHost, out var address)
            && !SourceRegistryNetworkPolicy.IsAddressAllowed(address, policy.AllowPrivateNetwork))
            throw new SourceRegistryOperationException("source_registry_endpoint_policy_denied", "The registry endpoint is not allowed by its private-network policy.");
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
            : exception.Code is "source_registry_endpoint_policy_denied" or "source_registry_address_blocked"
                ? SourceRegistryRefreshOutcome.PolicyDenied
            : exception.Unavailable
                ? SourceRegistryRefreshOutcome.Unavailable
                : SourceRegistryRefreshOutcome.Invalid;

    private static SourceRegistryRetrievalContext RetrievalContext(SourceRegistryRegistrationResource registration) =>
        new(
            ResourceScopeRef.Instance,
            registration.Address,
            registration.Definition.EndpointPolicy,
            registration.Definition.AuthenticationMode,
            registration.Definition.Credential);

    private Task AuditFailureAsync(string action, string reason, Guid? actor, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action, SecurityAuditOutcome.Failed, ActorPrincipalId: actor, ReasonCode: reason), cancellationToken);

    private Guid? ActorPrincipalId() => requestContext.IsInitialized ? requestContext.Current.PrincipalId : null;

    private static ResourceStatus SucceededStatus() => new() { ProvisioningState = ProvisioningState.Succeeded };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryNamePattern();

    [LoggerMessage(LogLevel.Information,
        "Source registry {RegistrationUid} refresh ({Trigger}) completed in {DurationMilliseconds} ms with {Result} after {RetryCount} retries; correlation {CorrelationId}")]
    private static partial void LogRefreshCompleted(
        ILogger logger,
        Guid registrationUid,
        SourceRegistryRefreshTrigger trigger,
        long durationMilliseconds,
        SourceRegistryRefreshOutcome result,
        int retryCount,
        string correlationId);

    [LoggerMessage(LogLevel.Warning, "Source registry {RegistrationUid} cache cleanup failed")]
    private static partial void LogCacheCleanupFailed(ILogger logger, Guid registrationUid, Exception exception);
}
