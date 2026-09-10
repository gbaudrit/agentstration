using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceChannelSnapshotService(
    SourceManagementService sources,
    SourceBindingManagementService bindings,
    SourceChannelCompatibilityEvaluator compatibility,
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    ISourceProviderMaterializer materializer,
    ISourceSnapshotArtifactStore artifacts,
    SourceMaterializationLimits limits,
    TimeProvider timeProvider,
    ResourceScopeOperationService scopeOperations)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

    public async Task<SourceChannelRefreshResult> RefreshAsync(
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetAsync(publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        return await RefreshCoreAsync(source, versionUid, channel, SourceRefreshTrigger.Manual, cancellationToken);
    }

    public async Task<SourceChannelRefreshResult> RefreshExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        return await RefreshCoreAsync(source, versionUid, channel, SourceRefreshTrigger.Manual, cancellationToken);
    }

    public async Task<SourceChannelRefreshResult> RefreshExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        return await RefreshCoreAsync(source, versionUid, channel, trigger, cancellationToken);
    }

    public async Task<SourceChannelObservedResource> RecordSkippedExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        string reasonCode,
        string reason,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        _ = await RequiredVersionAsync(source, versionUid, cancellationToken);
        var observed = await LoadObservedAsync(scopeRef, source, versionUid, channel, cancellationToken);
        return (await SaveObservedAsync(scopeRef, source, versionUid, channel, observed, timeProvider.GetUtcNow(),
            SourceChannelRefreshOutcome.Skipped, observed?.Value.Definition.CurrentSnapshotUid,
            observed?.Value.Definition.LastResolvedRevision, null, SourceRefreshTrigger.Scheduled,
            reasonCode, reason, cancellationToken)).Value;
    }

    public async Task<SourceChannelObservedResource> RecordScheduledFailureExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        _ = await RequiredVersionAsync(source, versionUid, cancellationToken);
        var observed = await LoadObservedAsync(scopeRef, source, versionUid, channel, cancellationToken);
        return (await SaveObservedAsync(scopeRef, source, versionUid, channel, observed, timeProvider.GetUtcNow(),
            SourceChannelRefreshOutcome.Failed, observed?.Value.Definition.CurrentSnapshotUid,
            observed?.Value.Definition.LastResolvedRevision, null, SourceRefreshTrigger.Scheduled,
            errorCode, errorMessage, cancellationToken)).Value;
    }

    public async Task<IReadOnlyList<SourceChannelSnapshotResource>> ListAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(ResourceKinds.SourceChannelSnapshot, scopeRef, AuthorizationPermissions.ResourcesRead, async token =>
        {
            var source = (await sources.GetExactAsync(scopeRef, publisher, name, token))?.Source
                ?? throw NotFound(ResourceKinds.Source, name, publisher);
            _ = await RequiredVersionAsync(source, versionUid, token);
            return (await store.ListExactAsync<SourceChannelSnapshotResource>(scopeRef, ResourceKinds.SourceChannelSnapshot, 0, int.MaxValue, token))
                .Where(value => value.Value.Definition.SourceVersionUid == versionUid
                    && string.Equals(value.Value.Definition.Channel, channel, StringComparison.Ordinal))
                .OrderByDescending(value => value.Value.Definition.MaterializedAt)
                .Select(value => value.Value)
                .ToArray();
        }, cancellationToken);

    public async Task<SourceChannelSnapshotResource?> GetAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        CancellationToken cancellationToken) =>
        (await ListAsync(scopeRef, publisher, name, versionUid, channel, cancellationToken))
            .SingleOrDefault(value => value.Uid == snapshotUid);

    public async Task<SourceChannelObservedResource?> GetObservedAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(ResourceKinds.SourceChannelObservedState, scopeRef, AuthorizationPermissions.ResourcesRead, async token =>
        {
            var source = (await sources.GetExactAsync(scopeRef, publisher, name, token))?.Source
                ?? throw NotFound(ResourceKinds.Source, name, publisher);
            _ = await RequiredVersionAsync(source, versionUid, token);
            return (await store.GetExactAsync<SourceChannelObservedResource>(
                ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceChannelObservedState, StateName(versionUid, channel)), token))?.Value;
        }, cancellationToken);

    public async Task<SourceChannelStatusView> GetStatusAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channelName,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(ResourceKinds.SourceChannelObservedState, scopeRef, AuthorizationPermissions.ResourcesRead, async token =>
        {
            var source = (await sources.GetExactAsync(scopeRef, publisher, name, token))?.Source
                ?? throw NotFound(ResourceKinds.Source, name, publisher);
            var version = await RequiredVersionAsync(source, versionUid, token);
            var channel = version.Definition.PublishedDefinition.Channels.SingleOrDefault(value =>
                string.Equals(value.Name, channelName, StringComparison.Ordinal))
                ?? throw Invalid("source_channel_missing", $"Source Version '{version.Definition.Version}' has no channel named '{channelName}'.");
            var observed = await LoadObservedAsync(scopeRef, source, versionUid, channelName, token);
            return new SourceChannelStatusView(channel.Name, compatibility.Evaluate(channel), observed?.Value);
        }, cancellationToken);

    private async Task<SourceChannelRefreshResult> RefreshCoreAsync(
        SourceResource source,
        Guid versionUid,
        string channelName,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var gate = refreshLocks.GetOrAdd($"{scopeRef}|{versionUid:N}|{channelName}", _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await scopeOperations.WriteAsync(ResourceKinds.SourceChannelSnapshot, scopeRef, AuthorizationPermissions.ResourcesWrite,
                token => RefreshLockedAsync(source, versionUid, channelName, trigger, token), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SourceChannelRefreshResult> RefreshLockedAsync(
        SourceResource source,
        Guid versionUid,
        string channelName,
        SourceRefreshTrigger trigger,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var version = await RequiredVersionAsync(source, versionUid, cancellationToken);
        var channel = version.Definition.PublishedDefinition.Channels.SingleOrDefault(value =>
            string.Equals(value.Name, channelName, StringComparison.Ordinal))
            ?? throw Invalid("source_channel_missing", $"Source Version '{version.Definition.Version}' has no channel named '{channelName}'.");
        var now = timeProvider.GetUtcNow();
        try
        {
            compatibility.RequireCompatible(channel);
            var status = await bindings.GetStatusExactAsync(scopeRef, source.Definition.Publisher, source.Name, versionUid, cancellationToken);
            var bindingStatus = status.Bindings.SingleOrDefault(value => value.Channels.Contains(channel.Name, StringComparer.Ordinal));
            if (bindingStatus is null || !string.Equals(bindingStatus.Status, "ready", StringComparison.Ordinal))
                throw Invalid("source_channel_binding_not_ready", bindingStatus?.Issues.FirstOrDefault()?.Message ?? $"Channel '{channel.Name}' has no ready Source Provider binding.");

            var configuration = await store.GetExactAsync<SourceConfigurationResource>(
                ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Name), cancellationToken)
                ?? throw NotFound(ResourceKinds.SourceConfiguration, source.Name, source.Namespace.Value);
            var selection = configuration.Value.Definition.Bindings.Single(value =>
                string.Equals(value.Name, channel.Provider.Binding, StringComparison.Ordinal));
            var provider = await references.ResolveAsync<SourceProviderResource>(
                selection.Target, source.Namespace, ResourceKinds.SourceProvider, scopeRef, cancellationToken)
                ?? throw Invalid("source_binding_provider_missing", $"Source Provider '{selection.Target.Name}' was not found.");
            var providerScope = RequireScope(provider.Value);
            var extension = await references.ResolveAsync<ExtensionRegistrationResource>(
                provider.Value.Definition.Extension, provider.Value.Namespace, ResourceKinds.ExtensionRegistration, providerScope, cancellationToken)
                ?? throw Invalid("source_binding_extension_missing", $"Extension registration for Source Provider '{provider.Value.Name}' was not found.");
            if (!extension.Value.Definition.Enabled)
                throw Invalid("source_binding_extension_disabled", $"Extension registration for Source Provider '{provider.Value.Name}' is disabled.");
            var invocation = new SourceProviderInvocation(
                extension.Value.Definition.Endpoint,
                provider.Value.Definition.ContributionId,
                channel.Configuration,
                extension.Value.Namespace,
                extension.Value.ScopeRef,
                extension.Value.Name,
                extension.Value.Definition.AuthenticationMode,
                extension.Value.Definition.Credential,
                extension.Value.Definition.ExpectedExtensionId);
            var resolved = await materializer.ResolveAsync(invocation, cancellationToken);
            var observed = await LoadObservedAsync(scopeRef, source, versionUid, channel.Name, cancellationToken);
            if (observed?.Value.Definition.CurrentSnapshotUid is { } currentUid
                && observed.Value.Definition.LastProviderUid == provider.Value.Uid
                && observed.Value.Definition.LastProviderGeneration == provider.Value.Generation
                && string.Equals(observed.Value.Definition.LastResolvedRevision, resolved.Revision, StringComparison.Ordinal))
            {
                var current = await store.GetByUidAsync<SourceChannelSnapshotResource>(currentUid, cancellationToken)
                    ?? throw new SourceRetrievalException("source_snapshot_missing", "The current immutable source snapshot is unavailable.");
                var updated = await SaveObservedAsync(scopeRef, source, versionUid, channel.Name, observed, now,
                    SourceChannelRefreshOutcome.Unchanged, currentUid, resolved.Revision, provider.Value, trigger, null, null, cancellationToken);
                return Result(SourceChannelRefreshOutcome.Unchanged, current.Value, updated.Value);
            }

            var materialized = await materializer.MaterializeAsync(invocation, resolved.Revision, limits, cancellationToken);
            if (!string.Equals(materialized.Revision, resolved.Revision, StringComparison.Ordinal))
                throw new SourceRetrievalException("source_revision_mismatch", "The Source Provider materialized a different revision than it resolved.");
            var artifact = await artifacts.SaveAsync(materialized, cancellationToken);
            var snapshotName = SnapshotName(versionUid, channel.Name, provider.Value, resolved.Revision);
            SourceChannelSnapshotResource snapshot;
            try
            {
                snapshot = (await store.CreateImmutableAsync(new SourceChannelSnapshotResource
                {
                    ApiVersion = ManagementApiVersions.CoreV1,
                    Kind = ResourceKinds.SourceChannelSnapshot,
                    Metadata = new() { Namespace = source.Namespace, Name = snapshotName },
                    ScopeRef = scopeRef,
                    Definition = new()
                    {
                        SourceUid = source.Uid,
                        SourceVersionUid = version.Uid,
                        SourceVersion = version.Definition.Version,
                        Channel = channel.Name,
                        RequestedConfiguration = channel.Configuration with { Values = channel.Configuration.Values.Clone() },
                        ResolvedRevision = resolved.Revision,
                        ResolvedIntegrity = resolved.Integrity,
                        Provider = Identity(provider.Value),
                        Artifact = artifact,
                        MaterializedAt = now
                    }
                }, cancellationToken)).Value;
            }
            catch (ControlPlaneConcurrencyException)
            {
                var existing = await store.GetExactAsync<SourceChannelSnapshotResource>(
                    ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceChannelSnapshot, snapshotName), cancellationToken);
                if (existing is null) throw;
                snapshot = existing.Value;
            }
            var saved = await SaveObservedAsync(scopeRef, source, versionUid, channel.Name, observed, now,
                SourceChannelRefreshOutcome.Created, snapshot.Uid, resolved.Revision, provider.Value, trigger, null, null, cancellationToken);
            return Result(SourceChannelRefreshOutcome.Created, snapshot, saved.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var observed = await LoadObservedAsync(scopeRef, source, versionUid, channel.Name, cancellationToken);
            var code = exception switch
            {
                SourceValidationException value => value.Code,
                SourceRetrievalException value => value.Code,
                _ => "source_channel_refresh_failed"
            };
            _ = await SaveObservedAsync(scopeRef, source, versionUid, channel.Name, observed, now,
                SourceChannelRefreshOutcome.Failed,
                observed?.Value.Definition.CurrentSnapshotUid,
                observed?.Value.Definition.LastResolvedRevision,
                null, trigger, code, exception.Message, cancellationToken);
            throw exception is SourceValidationException or SourceRetrievalException
                ? exception
                : new SourceRetrievalException(code, exception.Message, exception);
        }
    }

    private async Task<SourceVersionResource> RequiredVersionAsync(SourceResource source, Guid uid, CancellationToken cancellationToken) =>
        await sources.GetVersionExactAsync(RequireScope(source), source.Definition.Publisher, source.Name, uid, cancellationToken)
        ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, uid.ToString("D")));

    private Task<StoredResource<SourceChannelObservedResource>?> LoadObservedAsync(
        ResourceScopeRef scopeRef, SourceResource source, Guid versionUid, string channel, CancellationToken cancellationToken) =>
        store.GetExactAsync<SourceChannelObservedResource>(
            ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceChannelObservedState, StateName(versionUid, channel)), cancellationToken);

    private async Task<StoredResource<SourceChannelObservedResource>> SaveObservedAsync(
        ResourceScopeRef scopeRef,
        SourceResource source,
        Guid versionUid,
        string channel,
        StoredResource<SourceChannelObservedResource>? current,
        DateTimeOffset attemptedAt,
        SourceChannelRefreshOutcome outcome,
        Guid? snapshotUid,
        string? revision,
        SourceProviderResource? provider,
        SourceRefreshTrigger trigger,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var successful = outcome is SourceChannelRefreshOutcome.Created or SourceChannelRefreshOutcome.Unchanged;
        var failed = outcome == SourceChannelRefreshOutcome.Failed;
        var resource = new SourceChannelObservedResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceChannelObservedState,
            Metadata = new() { Namespace = source.Namespace, Name = StateName(versionUid, channel) },
            ScopeRef = scopeRef,
            Uid = current?.Value.Uid ?? Guid.Empty,
            Generation = current is null ? 0 : checked(current.Value.Generation + 1),
            Definition = new()
            {
                SourceUid = source.Uid,
                SourceVersionUid = versionUid,
                Channel = channel,
                LastAttemptAt = attemptedAt,
                LastOutcome = outcome,
                CurrentSnapshotUid = successful ? snapshotUid : current?.Value.Definition.CurrentSnapshotUid,
                LastResolvedRevision = successful ? revision : current?.Value.Definition.LastResolvedRevision,
                LastProviderUid = successful ? provider?.Uid : current?.Value.Definition.LastProviderUid,
                LastProviderGeneration = successful ? provider?.Generation : current?.Value.Definition.LastProviderGeneration,
                LastSuccessfulAt = successful ? attemptedAt : current?.Value.Definition.LastSuccessfulAt,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                LastTrigger = trigger,
                ConsecutiveFailures = failed ? checked((current?.Value.Definition.ConsecutiveFailures ?? 0) + 1) : 0
            }
        };
        var saved = await store.PutExactAsync(scopeRef, resource, current?.ETag, current is null, cancellationToken);
        _ = await store.CreateImmutableAsync(new SourceChannelRefreshRecordResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceChannelRefreshRecord,
            Metadata = new() { Namespace = source.Namespace, Name = $"refresh-{Guid.NewGuid():N}" },
            ScopeRef = scopeRef,
            Definition = new()
            {
                SourceUid = source.Uid,
                SourceVersionUid = versionUid,
                Channel = channel,
                AttemptedAt = attemptedAt,
                Outcome = outcome,
                Trigger = trigger,
                SnapshotUid = successful ? snapshotUid : current?.Value.Definition.CurrentSnapshotUid,
                ResolvedRevision = successful ? revision : current?.Value.Definition.LastResolvedRevision,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            }
        }, cancellationToken);
        return saved;
    }

    private static SourceChannelRefreshResult Result(SourceChannelRefreshOutcome outcome, SourceChannelSnapshotResource snapshot, SourceChannelObservedResource observed) =>
        new(outcome, snapshot, observed, new(snapshot.Uid, snapshot.Definition.SourceVersionUid, snapshot.Definition.Channel, snapshot.Definition.Artifact.Sha256));

    private static SourceProviderSnapshotIdentity Identity(SourceProviderResource provider) => new()
    {
        Uid = provider.Uid,
        ScopeRef = RequireScope(provider),
        Namespace = provider.Namespace,
        Name = provider.Name,
        Generation = provider.Generation,
        ContributionId = provider.Definition.ContributionId
    };

    private static string StateName(Guid versionUid, string channel) => $"{versionUid:N}-{Hash(channel)[..16]}";
    private static string SnapshotName(Guid versionUid, string channel, SourceProviderResource provider, string revision) =>
        $"{versionUid:N}-{Hash($"{channel}\n{provider.Uid:N}\n{provider.Generation}\n{revision}")}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw new InvalidOperationException($"Resource '{resource.Address}' has no ownership scope.");
    private static SourceValidationException Invalid(string code, string message) => new(code, message);
    private static ControlPlaneResourceNotFoundException NotFound(string kind, string name, string @namespace) =>
        new(new(kind, name, new ResourceNamespace(@namespace)));
}
