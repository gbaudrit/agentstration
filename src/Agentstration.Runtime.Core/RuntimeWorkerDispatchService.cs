using System.Collections.Concurrent;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed record RuntimeWorkerDispatchOptions
{
    public const string SectionName = "Agentstration:RuntimeWorker:Dispatch";

    public int MaximumLongPollSeconds { get; init; } = 30;
    public int MaximumWorkerCapacity { get; init; } = 64;

    public void Validate()
    {
        if (MaximumLongPollSeconds is < 1 or > 120)
            throw new InvalidOperationException("The AWP maximum long-poll duration must be between 1 and 120 seconds.");
        if (MaximumWorkerCapacity is < 1 or > 1024)
            throw new InvalidOperationException("The AWP maximum Worker capacity must be between 1 and 1024.");
    }
}

public sealed record RuntimeWorkerCapabilityRegistration(
    string RuntimeKind,
    string CapabilityVersion,
    IReadOnlySet<string> ExecutionMaterialVersions,
    string? RuntimeImplementationVersion);

public sealed record RuntimeWorkerRegistration(
    RuntimeWorkerId WorkerId,
    RuntimeWorkerSessionId WorkerSessionId,
    string SoftwareVersion,
    int MaximumConcurrentAssignments,
    IReadOnlyList<RuntimeWorkerCapabilityRegistration> Capabilities,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt);

public sealed class RuntimeWorkerDispatchService(
    RuntimeWorkerAssignmentService assignments,
    RuntimeAssignmentAvailabilitySignal availability,
    TimeProvider timeProvider,
    RuntimeWorkerDispatchOptions options)
{
    private readonly ConcurrentDictionary<RuntimeWorkerId, RuntimeWorkerRegistration> registrations = new();
    private readonly ConcurrentDictionary<RuntimeWorkerId, SemaphoreSlim> workerGates = new();

    public async Task<RuntimeWorkerRegistration> RegisterAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId sessionId,
        string softwareVersion,
        int maximumConcurrentAssignments,
        IReadOnlyList<RuntimeWorkerCapabilityRegistration> capabilities,
        CancellationToken cancellationToken)
    {
        options.Validate();
        ValidateRegistration(workerId, sessionId, softwareVersion, maximumConcurrentAssignments, capabilities);
        var gate = WorkerGate(workerId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var registration = new RuntimeWorkerRegistration(
                workerId,
                sessionId,
                softwareVersion,
                maximumConcurrentAssignments,
                capabilities,
                now,
                now);
            registration = registrations.AddOrUpdate(workerId, registration, (_, previous) =>
                previous.WorkerSessionId == sessionId
                    ? registration with { RegisteredAt = previous.RegisteredAt }
                    : registration);
            _ = await assignments.InterruptSupersededSessionsAsync(workerId, sessionId, cancellationToken);
            availability.Pulse();
            return registration;
        }
        finally { gate.Release(); }
    }

    public async Task<ClaimedRuntimeWorkerAssignment?> ClaimAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId sessionId,
        int availableCapacity,
        int maximumWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (availableCapacity < 0)
            throw new RuntimeWorkerDispatchException("invalid_request", "Available capacity cannot be negative.");
        if (maximumWaitSeconds < 0 || maximumWaitSeconds > options.MaximumLongPollSeconds)
            throw new RuntimeWorkerDispatchException("invalid_request", $"Maximum wait must be between 0 and {options.MaximumLongPollSeconds} seconds.");
        var registration = RequiredRegistration(workerId, sessionId);
        if (availableCapacity > registration.MaximumConcurrentAssignments)
            throw new RuntimeWorkerDispatchException("invalid_request", "Available capacity cannot exceed the registered Worker capacity.");
        if (availableCapacity == 0) return null;

        var deadline = timeProvider.GetUtcNow().AddSeconds(maximumWaitSeconds);
        while (true)
        {
            var claimed = await TryClaimAsync(workerId, sessionId, cancellationToken);
            if (claimed is not null || maximumWaitSeconds == 0) return claimed;

            var observedVersion = availability.Capture();
            claimed = await TryClaimAsync(workerId, sessionId, cancellationToken);
            if (claimed is not null) return claimed;
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return null;
            await availability.WaitForChangeAsync(observedVersion, remaining, cancellationToken);
        }
    }

    public RuntimeWorkerRegistration Touch(RuntimeWorkerId workerId, RuntimeWorkerSessionId sessionId)
    {
        while (true)
        {
            var registration = RequiredRegistration(workerId, sessionId);
            var refreshed = registration with { LastSeenAt = timeProvider.GetUtcNow() };
            if (registrations.TryUpdate(workerId, refreshed, registration)) return refreshed;
        }
    }

    private async Task<ClaimedRuntimeWorkerAssignment?> TryClaimAsync(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var gate = WorkerGate(workerId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var registration = RequiredRegistration(workerId, sessionId);
            foreach (var group in registration.Capabilities.GroupBy(value => value.RuntimeKind, StringComparer.Ordinal))
            {
                var claimed = await assignments.ClaimNextAsync(
                    registration.WorkerId,
                    registration.WorkerSessionId,
                    group.Key,
                    group.Select(value => value.CapabilityVersion).ToHashSet(StringComparer.Ordinal),
                    group.SelectMany(value => value.ExecutionMaterialVersions).ToHashSet(StringComparer.Ordinal),
                    registration.MaximumConcurrentAssignments,
                    cancellationToken);
                if (claimed is not null) return claimed;
            }
            return null;
        }
        finally { gate.Release(); }
    }

    private SemaphoreSlim WorkerGate(RuntimeWorkerId workerId) => workerGates.GetOrAdd(workerId, static _ => new(1, 1));

    private RuntimeWorkerRegistration RequiredRegistration(RuntimeWorkerId workerId, RuntimeWorkerSessionId sessionId)
    {
        if (!registrations.TryGetValue(workerId, out var registration))
            throw new RuntimeWorkerDispatchException("worker_not_registered", "The Worker session must register before claiming work.");
        if (registration.WorkerSessionId != sessionId)
            throw new RuntimeWorkerDispatchException("worker_session_superseded", "The Worker session has been superseded.");
        return registration;
    }

    private void ValidateRegistration(
        RuntimeWorkerId workerId,
        RuntimeWorkerSessionId sessionId,
        string softwareVersion,
        int maximumConcurrentAssignments,
        IReadOnlyList<RuntimeWorkerCapabilityRegistration> capabilities)
    {
        if (workerId.Value == Guid.Empty || sessionId.Value == Guid.Empty)
            throw new RuntimeWorkerDispatchException("invalid_request", "Worker and session identifiers must be non-empty.");
        if (string.IsNullOrWhiteSpace(softwareVersion) || softwareVersion.Length > 128)
            throw new RuntimeWorkerDispatchException("invalid_request", "Worker software version is required and limited to 128 characters.");
        if (maximumConcurrentAssignments < 1 || maximumConcurrentAssignments > options.MaximumWorkerCapacity)
            throw new RuntimeWorkerDispatchException("invalid_request", $"Worker capacity must be between 1 and {options.MaximumWorkerCapacity}.");
        if (capabilities.Count is < 1 or > 32)
            throw new RuntimeWorkerDispatchException("invalid_request", "At least one and at most 32 Runtime capabilities are required.");
        if (capabilities.Any(value => string.IsNullOrWhiteSpace(value.RuntimeKind)
            || value.RuntimeKind.Length > 128
            || string.IsNullOrWhiteSpace(value.CapabilityVersion)
            || value.CapabilityVersion.Length > 64
            || value.RuntimeImplementationVersion is { Length: > 128 }
            || value.ExecutionMaterialVersions.Count == 0
            || value.ExecutionMaterialVersions.Count > 32
            || value.ExecutionMaterialVersions.Any(version => string.IsNullOrWhiteSpace(version) || version.Length > 64)))
            throw new RuntimeWorkerDispatchException("invalid_request", "Every Runtime capability requires a kind, version and execution-material version.");
    }
}

public sealed class RuntimeAssignmentAvailabilitySignal
{
    private readonly object gate = new();
    private long version;
    private TaskCompletionSource signal = NewSignal();

    public long Capture()
    {
        lock (gate) return version;
    }

    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            version++;
            previous = signal;
            signal = NewSignal();
        }
        previous.TrySetResult();
    }

    public async Task WaitForChangeAsync(long observedVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task wait;
        lock (gate)
        {
            if (version != observedVersion) return;
            wait = signal.Task;
        }
        try { await wait.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class RuntimeWorkerDispatchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
