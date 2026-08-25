using Agentstration.Work.Contracts;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agentstration.Web.Console;

public enum WorkOperationsRealtimeState { Offline, Connecting, Live, Reconnecting }
public sealed record WorkOperationsRealtimeUpdate(Guid? TaskId, bool RequiresResynchronization = false);

public interface IWorkOperationsRealtimeClient : IAsyncDisposable
{
    WorkOperationsRealtimeState State { get; }
    event Func<WorkOperationsRealtimeUpdate, Task>? Updated;
    event Action? StateChanged;
    Task StartAsync(IEnumerable<string> workspaceIds, CancellationToken cancellationToken);
}

public sealed class WorkOperationsRealtimeClient(
    Uri hubUrl,
    ConsoleRealtimeSession realtimeSession,
    ILogger<WorkOperationsRealtimeClient> logger) : IWorkOperationsRealtimeClient
{
    private readonly HubConnection connection = new HubConnectionBuilder()
        .WithUrl(hubUrl, options => realtimeSession.Configure(hubUrl, options))
        .WithAutomaticReconnect()
        .Build();
    private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);
    private readonly List<IDisposable> handlers = [];
    private IReadOnlyList<string> workspaces = [];
    public WorkOperationsRealtimeState State { get; private set; } = WorkOperationsRealtimeState.Offline;
    public event Func<WorkOperationsRealtimeUpdate, Task>? Updated;
    public event Action? StateChanged;

    public async Task StartAsync(IEnumerable<string> workspaceIds, CancellationToken cancellationToken)
    {
        if (connection.State != HubConnectionState.Disconnected) return;
        workspaces = workspaceIds.Distinct(StringComparer.Ordinal).ToArray(); RegisterHandlers(); SetState(WorkOperationsRealtimeState.Connecting);
        connection.Reconnecting += _ => { SetState(WorkOperationsRealtimeState.Reconnecting); return Task.CompletedTask; };
        connection.Reconnected += async _ => { await JoinAsync(CancellationToken.None); SetState(WorkOperationsRealtimeState.Live); await DispatchAsync(new(null, true)); };
        connection.Closed += _ => { SetState(WorkOperationsRealtimeState.Offline); return Task.CompletedTask; };
        try { await connection.StartAsync(cancellationToken); await JoinAsync(cancellationToken); SetState(WorkOperationsRealtimeState.Live); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Work API realtime connection is unavailable."); SetState(WorkOperationsRealtimeState.Offline);
        }
    }

    private void RegisterHandlers()
    {
        if (handlers.Count > 0) return;
        handlers.Add(connection.On<TaskCreatedEvent>("TaskCreated", value => DispatchEventAsync(value.EventId, value.TaskId)));
        handlers.Add(connection.On<TaskStatusChangedEvent>("TaskStatusChanged", value => DispatchEventAsync(value.EventId, value.TaskId)));
        handlers.Add(connection.On<TaskActivityAddedEvent>("TaskActivityAdded", value => DispatchEventAsync(value.EventId, value.Activity.WorkTaskId.Value)));
        handlers.Add(connection.On<TaskResultAddedEvent>("TaskResultAdded", value => DispatchEventAsync(value.EventId, value.Result.WorkTaskId.Value)));
        handlers.Add(connection.On<TaskArtifactAddedEvent>("TaskArtifactAdded", value => DispatchEventAsync(value.EventId, value.Artifact.WorkTaskId)));
        handlers.Add(connection.On<PendingActionCreatedEvent>("PendingActionCreated", value => DispatchEventAsync(value.EventId, value.PendingAction.WorkTaskId)));
        handlers.Add(connection.On<PendingActionResolvedEvent>("PendingActionResolved", value => DispatchEventAsync(value.EventId, value.TaskId)));
        handlers.Add(connection.On<FlowRunStartedEvent>("FlowRunStarted", value => DispatchEventAsync(value.EventId, value.TaskId)));
        handlers.Add(connection.On<FlowRunCompletedEvent>("FlowRunCompleted", value => DispatchEventAsync(value.EventId, value.TaskId)));
    }

    private async Task JoinAsync(CancellationToken cancellationToken)
    {
        foreach (var workspace in workspaces) await connection.InvokeAsync("JoinWorkspace", workspace, cancellationToken);
    }

    private Task DispatchEventAsync(string eventId, Guid? taskId)
    {
        lock (eventIds)
        {
            if (!eventIds.Add(eventId)) return Task.CompletedTask;
            if (eventIds.Count > 2048) eventIds.Clear();
        }
        return DispatchAsync(new(taskId));
    }

    private async Task DispatchAsync(WorkOperationsRealtimeUpdate update)
    {
        if (Updated is null) return;
        foreach (var handler in Updated.GetInvocationList().Cast<Func<WorkOperationsRealtimeUpdate, Task>>()) await handler(update);
    }

    private void SetState(WorkOperationsRealtimeState state) { State = state; StateChanged?.Invoke(); }

    public async ValueTask DisposeAsync()
    {
        foreach (var handler in handlers) handler.Dispose();
        await connection.DisposeAsync();
    }
}
