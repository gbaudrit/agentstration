using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Workplace.Client;

public interface IWorkplaceApiClient
{
    Task<IReadOnlyList<WorkplaceWorkspaceResponse>> ListWorkspacesAsync(CancellationToken token);
    Task<WorkplaceWorkspaceResponse> GetWorkspaceAsync(string workspaceName, CancellationToken token);
    Task<EntryResponse> GetEntryAsync(EntryId entryId, CancellationToken token);
    Task<EntrySubmissionResponse> SubmitAsync(string workspaceName, EntryId entryId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token);
    Task<InteractionResponse> GetInteractionAsync(string workspaceName, Guid interactionId, CancellationToken token);
    Task<IReadOnlyList<InteractionResponse>> ListInteractionsAsync(string workspaceName, int take, CancellationToken token);
    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(string workspaceName, Guid interactionId, CancellationToken token);
    Task<AddConversationMessageResponse> AddMessageAsync(string workspaceName, Guid interactionId, string content, CancellationToken token);
    Task<PendingActionResolutionResponse> RespondAsync(string workspaceName, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token);
    Task<IReadOnlyList<WorkTaskResponse>> ListTasksAsync(string workspaceName, WorkTaskStatus? status, CancellationToken token);
    Task<WorkTaskResponse> GetTaskAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<WorkTaskResponse> PauseTaskAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<WorkTaskResponse> ResumeTaskAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<WorkTaskResponse> CancelTaskAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(string workspaceName, Guid taskId, CancellationToken token);
    Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(string workspaceName, bool unreadOnly, CancellationToken token);
    Task<int> GetUnreadCountAsync(string workspaceName, CancellationToken token);
    Task MarkNotificationReadAsync(string workspaceName, Guid notificationId, CancellationToken token);
    Task MarkAllNotificationsReadAsync(string workspaceName, CancellationToken token);
    Uri GetArtifactContentUri(string workspaceName, Guid taskId, Guid artifactId);
}

public sealed class WorkplaceApiClient(HttpClient httpClient) : IWorkplaceApiClient
{
    public async Task<IReadOnlyList<WorkplaceWorkspaceResponse>> ListWorkspacesAsync(CancellationToken token) => await httpClient.GetFromJsonAsync<WorkplaceWorkspaceResponse[]>($"api/workplace/workspaces?api-version={WorkplaceApiVersions.V20260805}", token) ?? [];
    public Task<WorkplaceWorkspaceResponse> GetWorkspaceAsync(string workspaceName, CancellationToken token) => GetAsync<WorkplaceWorkspaceResponse>($"api/workspaces/{E(workspaceName)}", token);
    public Task<EntryResponse> GetEntryAsync(EntryId entryId, CancellationToken token) => GetAsync<EntryResponse>($"api/{EntryPath(entryId)}", token);
    public async Task<EntrySubmissionResponse> SubmitAsync(string workspaceName, EntryId entryId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token) => await PostAsync<CreateInteractionRequest, EntrySubmissionResponse>($"api/workspaces/{E(workspaceName)}/{EntryPath(entryId)}/interactions", new CreateInteractionRequest(workspaceName, values), token);
    public Task<InteractionResponse> GetInteractionAsync(string workspaceName, Guid interactionId, CancellationToken token) => GetAsync<InteractionResponse>($"api/workspaces/{E(workspaceName)}/interactions/{interactionId}", token);
    public async Task<IReadOnlyList<InteractionResponse>> ListInteractionsAsync(string workspaceName, int take, CancellationToken token) => (await GetAsync<InteractionPageResponse>($"api/workspaces/{E(workspaceName)}/interactions?take={Math.Clamp(take, 1, 100)}", token)).Value;
    public async Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(string workspaceName, Guid interactionId, CancellationToken token) => await httpClient.GetFromJsonAsync<ConversationMessage[]>($"api/workspaces/{E(workspaceName)}/interactions/{interactionId}/messages", token) ?? [];
    public Task<AddConversationMessageResponse> AddMessageAsync(string workspaceName, Guid interactionId, string content, CancellationToken token) => PostAsync<AddConversationMessageRequest, AddConversationMessageResponse>($"api/workspaces/{E(workspaceName)}/interactions/{interactionId}/messages", new(content), token);
    public Task<PendingActionResolutionResponse> RespondAsync(string workspaceName, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token) => PostAsync<PendingActionResponseRequest, PendingActionResolutionResponse>($"api/workspaces/{E(workspaceName)}/interactions/{interactionId}/pending-actions/{actionId}/responses", new(resumeToken, values), token);
    public async Task<IReadOnlyList<WorkTaskResponse>> ListTasksAsync(string workspaceName, WorkTaskStatus? status, CancellationToken token) { var suffix = status is null ? string.Empty : $"?status={status}"; return (await GetAsync<WorkTaskPageResponse>($"api/workspaces/{E(workspaceName)}/tasks{suffix}", token)).Value; }
    public Task<WorkTaskResponse> GetTaskAsync(string workspaceName, Guid taskId, CancellationToken token) => GetAsync<WorkTaskResponse>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}", token);
    public Task<WorkTaskResponse> PauseTaskAsync(string workspaceName, Guid taskId, CancellationToken token) => PostEmptyAsync<WorkTaskResponse>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/pause", token);
    public Task<WorkTaskResponse> ResumeTaskAsync(string workspaceName, Guid taskId, CancellationToken token) => PostEmptyAsync<WorkTaskResponse>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/resume", token);
    public Task<WorkTaskResponse> CancelTaskAsync(string workspaceName, Guid taskId, CancellationToken token) => PostEmptyAsync<WorkTaskResponse>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/cancel", token);
    public async Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(string workspaceName, Guid taskId, CancellationToken token) => await httpClient.GetFromJsonAsync<WorkTaskActivity[]>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/activities", token) ?? [];
    public async Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(string workspaceName, Guid taskId, CancellationToken token) => await httpClient.GetFromJsonAsync<WorkTaskResult[]>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/results", token) ?? [];
    public async Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(string workspaceName, Guid taskId, CancellationToken token) => await httpClient.GetFromJsonAsync<WorkTaskArtifact[]>($"api/workspaces/{E(workspaceName)}/tasks/{taskId}/artifacts", token) ?? [];
    public async Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(string workspaceName, bool unreadOnly, CancellationToken token) => (await GetAsync<WorkNotificationPageResponse>($"api/workspaces/{E(workspaceName)}/notifications?unreadOnly={unreadOnly}", token)).Value;
    public async Task<int> GetUnreadCountAsync(string workspaceName, CancellationToken token) => (await GetAsync<UnreadNotificationCountResponse>($"api/workspaces/{E(workspaceName)}/notifications/unread-count", token)).Count;
    public async Task MarkNotificationReadAsync(string workspaceName, Guid notificationId, CancellationToken token) { using var response = await httpClient.PostAsync($"api/workspaces/{E(workspaceName)}/notifications/{notificationId}/read", null, token); response.EnsureSuccessStatusCode(); }
    public async Task MarkAllNotificationsReadAsync(string workspaceName, CancellationToken token) { using var response = await httpClient.PostAsync($"api/workspaces/{E(workspaceName)}/notifications/read-all", null, token); response.EnsureSuccessStatusCode(); }
    public Uri GetArtifactContentUri(string workspaceName, Guid taskId, Guid artifactId) => new(httpClient.BaseAddress!, $"api/workspaces/{E(workspaceName)}/tasks/{taskId}/artifacts/{artifactId}/content");

    private async Task<T> GetAsync<T>(string uri, CancellationToken token) => await httpClient.GetFromJsonAsync<T>(uri, token) ?? throw new InvalidOperationException($"The Work API returned no {typeof(T).Name} payload.");
    private async Task<TResponse> PostAsync<TRequest, TResponse>(string uri, TRequest body, CancellationToken token) { using var response = await httpClient.PostAsJsonAsync(uri, body, token); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<TResponse>(token))!; }
    private async Task<T> PostEmptyAsync<T>(string uri, CancellationToken token) { using var response = await httpClient.PostAsync(uri, null, token); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<T>(token))!; }
    private static string EntryPath(EntryId entryId) => entryId.Namespace.IsDefault ? $"entries/{E(entryId.Value)}" : $"namespaces/{E(entryId.Namespace.Value)}/entries/{E(entryId.Value)}";
    private static string E(string value) => Uri.EscapeDataString(value);
}

public sealed class WorkplaceRealtimeClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private readonly SemaphoreSlim subscriptionGate = new(1, 1);
    private string? workspaceId;
    private long lastSequence;

    public WorkplaceRealtimeClient(Uri hubUrl)
    {
        connection = new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build();
        connection.Reconnecting += _ => { StateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        connection.Reconnected += async _ =>
        {
            if (workspaceId is not null) await connection.InvokeAsync("SubscribeAsync", workspaceId, lastSequence);
            StateChanged?.Invoke(HubConnectionState.Connected);
        };
        connection.Closed += _ => { StateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };
    }

    public HubConnectionState State => connection.State;
    public event Action<HubConnectionState>? StateChanged;
    public IDisposable On<T>(string eventName, Action<T> handler) => connection.On(eventName, handler);
    public IDisposable OnWorkspaceChanged(Action<WorkplaceEventContract> handler)
    {
        var registrations = new List<IDisposable>();
        Register<InteractionUpdatedEvent>("InteractionUpdated");
        Register<MessageAddedEvent>("MessageAdded");
        Register<PendingActionCreatedEvent>("PendingActionCreated");
        Register<PendingActionResolvedEvent>("PendingActionResolved");
        Register<TaskCreatedEvent>("TaskCreated");
        Register<FlowRunStartedEvent>("FlowRunStarted");
        Register<FlowRunCompletedEvent>("FlowRunCompleted");
        Register<TaskStatusChangedEvent>("TaskStatusChanged");
        Register<TaskActivityAddedEvent>("TaskActivityAdded");
        Register<TaskResultAddedEvent>("TaskResultAdded");
        Register<TaskArtifactAddedEvent>("TaskArtifactAdded");
        Register<NotificationCreatedEvent>("NotificationCreated");
        Register<NotificationUpdatedEvent>("NotificationUpdated");
        Register<UnreadNotificationCountChangedEvent>("UnreadNotificationCountChanged");
        return new CompositeRegistration(registrations);

        void Register<T>(string name) where T : WorkplaceEventContract => registrations.Add(On<T>(name, value =>
        {
            InterlockedExtensions.Max(ref lastSequence, value.Sequence);
            handler(value);
        }));
    }

    public async Task StartAsync(string workspace, long afterSequence, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        await subscriptionGate.WaitAsync(token);
        try
        {
            if (connection.State == HubConnectionState.Disconnected) await connection.StartAsync(token);
            if (workspaceId is not null && !string.Equals(workspaceId, workspace, StringComparison.Ordinal))
            {
                await connection.InvokeAsync("UnsubscribeAsync", workspaceId, token);
                lastSequence = afterSequence;
            }
            else
            {
                lastSequence = Math.Max(lastSequence, afterSequence);
            }

            workspaceId = workspace;
            await connection.InvokeAsync("SubscribeAsync", workspace, lastSequence, token);
            StateChanged?.Invoke(connection.State);
        }
        finally
        {
            subscriptionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        subscriptionGate.Dispose();
    }

    private sealed class CompositeRegistration(IReadOnlyList<IDisposable> registrations) : IDisposable
    {
        public void Dispose() { foreach (var registration in registrations) registration.Dispose(); }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long value)
        {
            long current;
            do { current = Volatile.Read(ref location); if (current >= value) return; }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }
}

public static class WorkplaceClientServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWorkplaceClient(this IServiceCollection services, Uri apiBaseUrl, Uri hubUrl)
    {
        services.AddHttpClient<IWorkplaceApiClient, WorkplaceApiClient>(client => client.BaseAddress = apiBaseUrl);
        services.AddScoped(_ => new WorkplaceRealtimeClient(hubUrl)); return services;
    }
}
