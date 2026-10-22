using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeAssignmentLeaseGuardRegistry(
    TimeProvider timeProvider,
    RuntimeWorkerLeaseOptions options) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<RuntimeAssignmentOwnershipKey, HashSet<RuntimeAssignmentLeaseGuard>> guards = [];
    private bool disposed;

    public RuntimeAssignmentLeaseGuard Register(
        RuntimeAssignmentOwnershipProof proof,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var key = RuntimeAssignmentOwnershipKey.From(proof);
        var guard = new RuntimeAssignmentLeaseGuard(
            this, key, timeProvider, options.MinimumSideEffectLeaseRemaining, cancellationToken);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!guards.TryGetValue(key, out var registered))
                {
                    registered = [];
                    guards.Add(key, registered);
                }
                registered.Add(guard);
            }

            guard.Renew(leaseExpiresAt, cancellationRequested: false);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    public void Renew(
        RuntimeAssignmentOwnershipProof proof,
        DateTimeOffset leaseExpiresAt,
        bool cancellationRequested)
    {
        foreach (var guard in Snapshot(RuntimeAssignmentOwnershipKey.From(proof)))
            guard.Renew(leaseExpiresAt, cancellationRequested);
    }

    public void Revoke(RuntimeAssignmentOwnershipProof proof) =>
        Revoke(RuntimeAssignmentOwnershipKey.From(proof));

    public void Revoke(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId)
    {
        RuntimeAssignmentLeaseGuard[] snapshot;
        lock (gate)
        {
            snapshot = guards
                .Where(value => value.Key.WorkspaceId == workspaceId && value.Key.AssignmentId == assignmentId)
                .SelectMany(value => value.Value)
                .ToArray();
        }
        foreach (var guard in snapshot) guard.Cancel();
    }

    public void Dispose()
    {
        RuntimeAssignmentLeaseGuard[] snapshot;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            snapshot = guards.Values.SelectMany(value => value).ToArray();
            guards.Clear();
        }
        foreach (var guard in snapshot) guard.Cancel();
    }

    internal void Unregister(RuntimeAssignmentOwnershipKey key, RuntimeAssignmentLeaseGuard guard)
    {
        lock (gate)
        {
            if (!guards.TryGetValue(key, out var registered)) return;
            registered.Remove(guard);
            if (registered.Count == 0) guards.Remove(key);
        }
    }

    private RuntimeAssignmentLeaseGuard[] Snapshot(RuntimeAssignmentOwnershipKey key)
    {
        lock (gate)
            return guards.TryGetValue(key, out var registered) ? [.. registered] : [];
    }

    private void Revoke(RuntimeAssignmentOwnershipKey key)
    {
        foreach (var guard in Snapshot(key)) guard.Cancel();
    }
}

public sealed class RuntimeAssignmentLeaseGuard : IDisposable
{
    private readonly RuntimeAssignmentLeaseGuardRegistry owner;
    private readonly RuntimeAssignmentOwnershipKey key;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan safetyMargin;
    private readonly CancellationTokenSource cancellation;
    private readonly ITimer timer;
    private readonly object gate = new();
    private DateTimeOffset leaseExpiresAt = DateTimeOffset.MinValue;
    private bool disposed;
    private bool cancellationStarted;
    private bool cancellationCompleted;
    private bool resourcesDisposed;

    internal RuntimeAssignmentLeaseGuard(
        RuntimeAssignmentLeaseGuardRegistry owner,
        RuntimeAssignmentOwnershipKey key,
        TimeProvider timeProvider,
        TimeSpan safetyMargin,
        CancellationToken cancellationToken)
    {
        this.owner = owner;
        this.key = key;
        this.timeProvider = timeProvider;
        this.safetyMargin = safetyMargin;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer = timeProvider.CreateTimer(static state => ((RuntimeAssignmentLeaseGuard)state!).Cancel(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => cancellation.Token;

    internal void Renew(DateTimeOffset renewedLeaseExpiresAt, bool cancellationRequested)
    {
        if (cancellationRequested)
        {
            Cancel();
            return;
        }

        var cancel = false;
        lock (gate)
        {
            if (disposed || renewedLeaseExpiresAt <= leaseExpiresAt) return;
            leaseExpiresAt = renewedLeaseExpiresAt;
            var dueTime = leaseExpiresAt - safetyMargin - timeProvider.GetUtcNow();
            if (dueTime <= TimeSpan.Zero)
                cancel = true;
            else
                timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        if (cancel) Cancel();
    }

    internal void Cancel()
    {
        lock (gate)
        {
            if (disposed || cancellationStarted) return;
            cancellationStarted = true;
        }

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            var cleanup = false;
            lock (gate)
            {
                cancellationCompleted = true;
                cleanup = disposed;
            }
            if (cleanup) DisposeResources();
        }
    }

    public void Dispose()
    {
        var cleanup = false;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cleanup = !cancellationStarted || cancellationCompleted;
        }
        owner.Unregister(key, this);
        if (cleanup) DisposeResources();
    }

    private void DisposeResources()
    {
        lock (gate)
        {
            if (resourcesDisposed) return;
            resourcesDisposed = true;
            timer.Dispose();
            cancellation.Dispose();
        }
    }
}

internal readonly record struct RuntimeAssignmentOwnershipKey(
    WorkspaceId WorkspaceId,
    RuntimeAssignmentId AssignmentId,
    RuntimeAssignmentAttemptId AttemptId,
    RuntimeWorkerId WorkerId,
    RuntimeWorkerSessionId WorkerSessionId,
    long FencingGeneration)
{
    public static RuntimeAssignmentOwnershipKey From(RuntimeAssignmentOwnershipProof proof) => new(
        proof.WorkspaceId,
        proof.AssignmentId,
        proof.AttemptId,
        proof.WorkerId,
        proof.WorkerSessionId,
        proof.FencingGeneration);
}
