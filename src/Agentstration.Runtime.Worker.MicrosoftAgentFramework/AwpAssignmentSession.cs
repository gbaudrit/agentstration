using System.Text.Json;
using Agentstration.Awp.Abstractions;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpAssignmentSession
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan safetyMargin;
    private readonly SemaphoreSlim eventGate = new(1, 1);
    private DateTimeOffset leaseExpiresAt;
    private TimeSpan serverOffset;
    private long eventSequence;
    private AwpExecutionEvent? pendingEvent;

    public AwpAssignmentSession(AwpClient client, AwpRunAssignment assignment, DateTimeOffset serverTime,
        TimeProvider timeProvider, TimeSpan safetyMargin)
    {
        Client = client;
        Assignment = assignment;
        this.timeProvider = timeProvider;
        this.safetyMargin = safetyMargin;
        leaseExpiresAt = assignment.Ownership.LeaseExpiresAt;
        serverOffset = serverTime - timeProvider.GetUtcNow();
        eventSequence = assignment.InitialEventSequence;
        Context = new(client.WorkerId, client.SessionId, assignment.Scope, assignment.AssignmentId,
            assignment.AttemptId, assignment.Ownership.Token, assignment.Ownership.FencingGeneration);
    }

    public AwpRunAssignment Assignment { get; }
    public AwpClient Client { get; }
    public AwpAssignmentCommandContext Context { get; }
    public long LastEventSequence => Interlocked.Read(ref eventSequence);

    public void ApplyHeartbeat(AwpHeartbeatResponse heartbeat)
    {
        lock (gate)
        {
            serverOffset = heartbeat.ServerTime - timeProvider.GetUtcNow();
            leaseExpiresAt = heartbeat.LeaseExpiresAt;
        }
    }

    public bool CanStartMutation()
    {
        lock (gate) return timeProvider.GetUtcNow() + serverOffset + safetyMargin < leaseExpiresAt;
    }

    public void EnsureCanStartMutation()
    {
        if (!CanStartMutation())
            throw new AwpLeaseUnsafeException("The assignment lease is too close to expiry for another mutation.");
    }

    public async Task EnsureCanStartMutationAsync(CancellationToken cancellationToken)
    {
        if (CanStartMutation()) return;
        var heartbeat = await Client.HeartbeatAsync(new(Context, LastEventSequence), cancellationToken);
        ApplyHeartbeat(heartbeat);
        EnsureCanStartMutation();
    }

    public async Task AppendEventAsync(AwpExecutionEventKind kind, AwpExecutionLocation location,
        JsonElement? payload, AwpToolCallId? toolCallId, CancellationToken cancellationToken)
    {
        await eventGate.WaitAsync(cancellationToken);
        try
        {
            if (pendingEvent is not null)
                await SendPendingEventAsync(cancellationToken);

            await EnsureCanStartMutationAsync(cancellationToken);
            pendingEvent = new(new(Guid.NewGuid()), LastEventSequence + 1, timeProvider.GetUtcNow(),
                kind, location, toolCallId, payload);
            await SendPendingEventAsync(cancellationToken);
        }
        finally
        {
            eventGate.Release();
        }
    }

    private async Task SendPendingEventAsync(CancellationToken cancellationToken)
    {
        await EnsureCanStartMutationAsync(cancellationToken);
        var response = await Client.AppendEventsAsync(new(Context, [pendingEvent!]), cancellationToken);
        Interlocked.Exchange(ref eventSequence, response.AcceptedThroughSequence);
        pendingEvent = null;
    }
}

internal sealed class AwpLeaseUnsafeException(string message) : Exception(message);
