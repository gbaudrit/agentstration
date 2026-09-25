using Agentstration.Bootstrap.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure;

public interface IInstanceInitializationCoordinator
{
    bool IsReady { get; }
    Task RunAsync(Func<CancellationToken, Task> initialize, CancellationToken cancellationToken);
}

public sealed class InstanceInitializationOptions
{
    public const string ConfigurationSection = "Agentstration:Initialization";
    public int TargetVersion { get; set; } = 1;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

internal sealed class InstanceInitializationCoordinator(
    IResourceStore store,
    InstanceInitializationOptions options,
    TimeProvider timeProvider,
    ILogger<InstanceInitializationCoordinator> logger) : IInstanceInitializationCoordinator
{
    internal const string ResourceName = "platform-bootstrap";
    private const int MaximumErrorLength = 2048;
    private readonly string ownerInstanceId = Guid.NewGuid().ToString("N");
    private volatile bool isReady;

    public bool IsReady => isReady;

    public async Task RunAsync(Func<CancellationToken, Task> initialize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ValidateOptions();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(cancellationToken);
            if (current is not null && current.Value.Definition.TargetVersion > options.TargetVersion)
                throw new InvalidOperationException($"Instance initialization target version {current.Value.Definition.TargetVersion} is newer than the supported version {options.TargetVersion}.");
            if (current?.Value.Definition is { Status: InstanceInitializationStatus.Ready } ready
                && ready.TargetVersion == options.TargetVersion)
            {
                isReady = true;
                return;
            }

            var lease = await TryAcquireAsync(current, cancellationToken);
            if (lease is not null)
            {
                await RunAsOwnerAsync(lease, initialize, cancellationToken);
                isReady = true;
                return;
            }

            await Task.Delay(options.PollInterval, timeProvider, cancellationToken);
        }
    }

    private async Task<StoredResource<InstanceInitializationResource>?> TryAcquireAsync(
        StoredResource<InstanceInitializationResource>? current,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (current is null)
        {
            try
            {
                return await store.PutExactAsync(
                    ResourceScopeRef.Instance,
                    Resource(1, now),
                    null,
                    true,
                    cancellationToken);
            }
            catch (ResourceConcurrencyException)
            {
                if (await ReadAsync(cancellationToken) is null) throw;
                return null;
            }
        }

        var state = current.Value.Definition;
        if (state.Status == InstanceInitializationStatus.Initializing
            && state.LeaseExpiresAt is { } expiresAt
            && expiresAt > now)
            return null;

        try
        {
            return await store.PutExactAsync(
                ResourceScopeRef.Instance,
                current.Value with
                {
                    Generation = checked(current.Value.Generation + 1),
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
                    Definition = state with
                    {
                        TargetVersion = options.TargetVersion,
                        Status = InstanceInitializationStatus.Initializing,
                        OwnerInstanceId = ownerInstanceId,
                        LeaseExpiresAt = now.Add(options.LeaseDuration),
                        FencingToken = checked(state.FencingToken + 1),
                        UpdatedAt = now,
                        LastError = null
                    }
                },
                current.ETag,
                false,
                cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            return null;
        }
    }

    private async Task RunAsOwnerAsync(
        StoredResource<InstanceInitializationResource> lease,
        Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Instance {OwnerInstanceId} acquired initialization version {TargetVersion} with fencing token {FencingToken}.",
                ownerInstanceId,
                options.TargetVersion,
                lease.Value.Definition.FencingToken);
        }

        using var ownershipLost = new CancellationTokenSource();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownershipLost.Token);
        Exception? heartbeatFailure = null;
        var heartbeat = RenewLeaseAsync(lease.Value.Definition.FencingToken, ownershipLost, exception => heartbeatFailure = exception);
        try
        {
            await initialize(work.Token);
            ownershipLost.Cancel();
            await heartbeat;
            if (heartbeatFailure is not null)
                throw new InvalidOperationException("The instance initialization lease was lost.", heartbeatFailure);
            await CompleteAsync(lease.Value.Definition.FencingToken, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ownershipLost.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ownershipLost.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            await FailAsync(lease.Value.Definition.FencingToken, exception, cancellationToken);
            throw;
        }
    }

    private async Task RenewLeaseAsync(long fencingToken, CancellationTokenSource ownershipLost, Action<Exception> failed)
    {
        var interval = TimeSpan.FromTicks(options.LeaseDuration.Ticks / 3);
        try
        {
            while (true)
            {
                await Task.Delay(interval, timeProvider, ownershipLost.Token);
                var current = await RequireOwnedAsync(fencingToken, ownershipLost.Token);
                var now = timeProvider.GetUtcNow();
                await store.PutExactAsync(
                    ResourceScopeRef.Instance,
                    current.Value with
                    {
                        Generation = checked(current.Value.Generation + 1),
                        Definition = current.Value.Definition with
                        {
                            LeaseExpiresAt = now.Add(options.LeaseDuration),
                            UpdatedAt = now
                        }
                    },
                    current.ETag,
                    false,
                    ownershipLost.Token);
            }
        }
        catch (OperationCanceledException) when (ownershipLost.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failed(exception);
            ownershipLost.Cancel();
        }
    }

    private async Task CompleteAsync(long fencingToken, CancellationToken cancellationToken)
    {
        var current = await RequireOwnedAsync(fencingToken, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await store.PutExactAsync(
            ResourceScopeRef.Instance,
            current.Value with
            {
                Generation = checked(current.Value.Generation + 1),
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                Definition = current.Value.Definition with
                {
                    Status = InstanceInitializationStatus.Ready,
                    OwnerInstanceId = null,
                    LeaseExpiresAt = null,
                    UpdatedAt = now,
                    LastError = null
                }
            },
            current.ETag,
            false,
            cancellationToken);
    }

    private async Task FailAsync(long fencingToken, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            var current = await RequireOwnedAsync(fencingToken, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var error = exception.Message.Length <= MaximumErrorLength
                ? exception.Message
                : exception.Message[..MaximumErrorLength];
            await store.PutExactAsync(
                ResourceScopeRef.Instance,
                current.Value with
                {
                    Generation = checked(current.Value.Generation + 1),
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed },
                    Definition = current.Value.Definition with
                    {
                        Status = InstanceInitializationStatus.Failed,
                        OwnerInstanceId = null,
                        LeaseExpiresAt = null,
                        UpdatedAt = now,
                        LastError = error
                    }
                },
                current.ETag,
                false,
                cancellationToken);
        }
        catch (Exception failure) when (failure is ResourceConcurrencyException or InvalidOperationException)
        {
            logger.LogWarning(failure, "Could not persist the failed instance initialization state because ownership changed.");
        }
    }

    private async Task<StoredResource<InstanceInitializationResource>> RequireOwnedAsync(long fencingToken, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException("The instance initialization record no longer exists.");
        var state = current.Value.Definition;
        if (state.Status != InstanceInitializationStatus.Initializing
            || state.TargetVersion != options.TargetVersion
            || state.FencingToken != fencingToken
            || !string.Equals(state.OwnerInstanceId, ownerInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The instance initialization lease is no longer owned by this process.");
        return current;
    }

    private Task<StoredResource<InstanceInitializationResource>?> ReadAsync(CancellationToken cancellationToken) =>
        store.GetExactAsync<InstanceInitializationResource>(Address(), cancellationToken);

    private InstanceInitializationResource Resource(long fencingToken, DateTimeOffset now) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = BootstrapKinds.InstanceInitialization,
        Metadata = new ResourceMetadata { Name = ResourceName },
        ScopeRef = ResourceScopeRef.Instance,
        Generation = 1,
        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
        Definition = new InstanceInitializationProperties
        {
            TargetVersion = options.TargetVersion,
            Status = InstanceInitializationStatus.Initializing,
            OwnerInstanceId = ownerInstanceId,
            LeaseExpiresAt = now.Add(options.LeaseDuration),
            FencingToken = fencingToken,
            UpdatedAt = now
        }
    };

    private static ScopedResourceAddress Address() => ScopedResourceAddress.Create(
        ResourceScopeRef.Instance,
        ResourceNamespace.Default,
        BootstrapKinds.InstanceInitialization,
        ResourceName);

    private void ValidateOptions()
    {
        if (options.TargetVersion < 1) throw new InvalidOperationException("The instance initialization target version must be positive.");
        if (options.LeaseDuration < TimeSpan.FromSeconds(3)) throw new InvalidOperationException("The instance initialization lease must be at least three seconds.");
        if (options.PollInterval <= TimeSpan.Zero || options.PollInterval >= options.LeaseDuration)
            throw new InvalidOperationException("The instance initialization poll interval must be positive and shorter than the lease.");
    }
}
