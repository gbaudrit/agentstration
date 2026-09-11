using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceRefreshScheduler(
    SourceManagementService sources,
    SourceChannelSnapshotService channels,
    IRequestContextScopeFactory scopes,
    TimeProvider timeProvider,
    SourceRegistryManagementService? registries = null)
{
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var now = timeProvider.GetUtcNow();
        foreach (var source in await sources.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopeRef = source.Source.ScopeRef
                ?? throw new InvalidOperationException($"Source '{source.Source.Address}' has no ownership scope.");
            await RefreshSourceIfDueAsync(source, scopeRef, now, cancellationToken);
            var current = await sources.GetExactAsync(scopeRef, source.Source.Definition.Publisher,
                source.Source.Name, cancellationToken) ?? source;
            await RefreshChannelsIfDueAsync(current, scopeRef, now, cancellationToken);
        }

        await ScanDueRegistriesAsync(now, cancellationToken);
    }

    public async Task RunDueRegistriesAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        await ScanDueRegistriesAsync(timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task ScanDueRegistriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (registries is null) return;
        foreach (var registry in await registries.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registration = registry.Registration;
            var policy = registration.Definition.RefreshPolicy;
            if (!registration.Definition.Enabled || !policy.PeriodicEnabled) continue;

            var observed = registry.Observed.Definition;
            if (observed.LastAttemptedAt is { } lastAttemptedAt
                && GetNextDue(lastAttemptedAt, observed.ConsecutiveFailures, policy,
                    $"registry|{registration.Uid:N}") > now)
                continue;

            using var timeout = new CancellationTokenSource(policy.Timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                _ = await registries.RefreshAsync(registration.Name, SourceRegistryRefreshTrigger.Scheduled,
                    observed.ConsecutiveFailures, observed.LastAttemptedAt, linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await registries.RecordScheduledFailureAsync(registration.Name, "source_registry_refresh_timeout",
                    "The scheduled Source registry refresh timed out.", observed.ConsecutiveFailures,
                    cancellationToken);
            }
            catch (SourceRegistryOperationException)
            {
                // Expected failures are persisted and retried according to the registration policy.
            }
        }
    }

    private async Task RefreshSourceIfDueAsync(
        SourceView source,
        ResourceScopeRef scopeRef,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = source.Configuration.Definition.Refresh.Source;
        if (!policy.Enabled || source.Configuration.Definition.Origin is null
            || !IsDue(source.Observed.Definition.LastAttemptAt, source.Observed.Definition.ConsecutiveFailures,
                policy, $"{scopeRef}|{source.Source.Namespace}|{source.Source.Name}|source", now))
            return;

        await ExecuteBoundedAsync(policy,
            token => sources.RefreshExactAsync(scopeRef, source.Source.Definition.Publisher, source.Source.Name,
                SourceRefreshTrigger.Scheduled, token),
            token => sources.RecordScheduledFailureExactAsync(scopeRef, source.Source.Definition.Publisher,
                source.Source.Name, "source_refresh_timeout", "The scheduled Source refresh timed out.", token),
            cancellationToken);
    }

    private async Task RefreshChannelsIfDueAsync(
        SourceView source,
        ResourceScopeRef scopeRef,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var versionUid = source.Observed.Definition.LastSuccessfulVersionUid;
        if (versionUid is null) return;
        var version = await sources.GetVersionExactAsync(scopeRef, source.Source.Definition.Publisher,
            source.Source.Name, versionUid.Value, cancellationToken);
        if (version is null) return;

        foreach (var channel in version.Definition.PublishedDefinition.Channels)
        {
            var refresh = source.Configuration.Definition.Refresh;
            var policy = refresh.ChannelOverrides.TryGetValue(channel.Name, out var configured)
                ? configured
                : refresh.Channels;
            if (!policy.Enabled) continue;

            var status = await channels.GetStatusAsync(scopeRef, source.Source.Definition.Publisher,
                source.Source.Name, version.Uid, channel.Name, cancellationToken);
            var observed = status.Refresh?.Definition;
            if (observed is not null && !IsDue(observed.LastAttemptAt, observed.ConsecutiveFailures, policy,
                    $"{scopeRef}|{source.Source.Namespace}|{source.Source.Name}|{version.Uid:N}|{channel.Name}", now))
                continue;

            if (status.Compatibility.Status != SourceChannelCompatibilityStatus.Compatible)
            {
                await channels.RecordSkippedExactAsync(scopeRef, source.Source.Definition.Publisher,
                    source.Source.Name, version.Uid, channel.Name,
                    status.Compatibility.ReasonCode ?? "source_channel_compatibility_unknown",
                    status.Compatibility.Reason ?? "The Channel is not currently compatible.", cancellationToken);
                continue;
            }

            await ExecuteBoundedAsync(policy,
                token => channels.RefreshExactAsync(scopeRef, source.Source.Definition.Publisher,
                    source.Source.Name, version.Uid, channel.Name, SourceRefreshTrigger.Scheduled, token),
                token => channels.RecordScheduledFailureExactAsync(scopeRef, source.Source.Definition.Publisher,
                    source.Source.Name, version.Uid, channel.Name, "source_channel_refresh_timeout",
                    "The scheduled Channel refresh timed out.", token),
                cancellationToken);
        }
    }

    private async Task ExecuteBoundedAsync<T>(
        SourceRefreshPolicy policy,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> recordTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(policy.TimeoutSeconds), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            _ = await operation(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await recordTimeout(cancellationToken);
        }
        catch (SourceValidationException)
        {
            // Expected failures are persisted by the refresh services and retried according to policy.
        }
        catch (SourceRetrievalException)
        {
            // Expected failures are persisted by the refresh services and retried according to policy.
        }
        catch (SourceVersionConflictException)
        {
            // A conflicting immutable version is persisted as a rejected Source attempt.
        }
    }

    internal static bool IsDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        SourceRefreshPolicy policy,
        string scheduleKey,
        DateTimeOffset now)
        => GetNextDue(lastAttempt, consecutiveFailures, policy, scheduleKey) <= now;

    public static DateTimeOffset GetNextDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        SourceRefreshPolicy policy,
        string scheduleKey)
        => SourceRefreshSchedule.GetNextDue(lastAttempt, consecutiveFailures, policy, scheduleKey);

    public static DateTimeOffset GetNextDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        SourceRegistryRefreshPolicy policy,
        string scheduleKey)
        => SourceRefreshSchedule.GetNextDue(lastAttempt, consecutiveFailures, policy, scheduleKey);
}
