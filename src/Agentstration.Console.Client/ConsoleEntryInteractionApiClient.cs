using System.Net.Http.Json;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public interface IConsoleEntryInteractionApiClient
{
    Task<IReadOnlyList<InteractionResponse>> ListInteractionsAsync(Guid workspaceId, int take, CancellationToken cancellationToken);
    Task<InteractionResponse> GetInteractionAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingActionContract>> ListPendingActionsAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken);
    Task<AddConversationMessageResponse> AddMessageAsync(Guid workspaceId, Guid interactionId, string content, CancellationToken cancellationToken);
    Task<PendingActionResolutionResponse> RespondAsync(Guid workspaceId, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, CancellationToken cancellationToken);
    Task<WorkTaskResponse> GetTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken);
    Task<WorkTaskResponse> CancelTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken);
    Uri GetArtifactContentUri(Guid workspaceId, Guid taskId, Guid artifactId);
}

public sealed class ConsoleEntryInteractionApiClient(HttpClient httpClient) : IConsoleEntryInteractionApiClient
{
    public async Task<IReadOnlyList<InteractionResponse>> ListInteractionsAsync(Guid workspaceId, int take, CancellationToken cancellationToken) =>
        (await ApiResponse.ReadAsync<InteractionPageResponse>(httpClient, $"{WorkspacePath(workspaceId)}/interactions?take={Math.Clamp(take, 1, 100)}", cancellationToken)).Value;

    public Task<InteractionResponse> GetInteractionAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<InteractionResponse>(httpClient, $"{WorkspacePath(workspaceId)}/interactions/{interactionId:D}", cancellationToken);

    public async Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<ConversationMessage[]>(httpClient, $"{WorkspacePath(workspaceId)}/interactions/{interactionId:D}/messages", cancellationToken);

    public async Task<IReadOnlyList<PendingActionContract>> ListPendingActionsAsync(Guid workspaceId, Guid interactionId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<PendingActionContract[]>(httpClient, $"{WorkspacePath(workspaceId)}/interactions/{interactionId:D}/pending-actions", cancellationToken);

    public Task<AddConversationMessageResponse> AddMessageAsync(Guid workspaceId, Guid interactionId, string content, CancellationToken cancellationToken) =>
        PostAsync<AddConversationMessageRequest, AddConversationMessageResponse>(
            $"{WorkspacePath(workspaceId)}/interactions/{interactionId:D}/messages",
            new(content), cancellationToken);

    public Task<PendingActionResolutionResponse> RespondAsync(Guid workspaceId, Guid interactionId, Guid actionId, string resumeToken, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, CancellationToken cancellationToken) =>
        PostAsync<PendingActionResponseRequest, PendingActionResolutionResponse>(
            $"{WorkspacePath(workspaceId)}/interactions/{interactionId:D}/pending-actions/{actionId:D}/responses",
            new(resumeToken, values), cancellationToken);

    public Task<WorkTaskResponse> GetTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<WorkTaskResponse>(httpClient, $"{WorkspacePath(workspaceId)}/tasks/{taskId:D}", cancellationToken);

    public Task<WorkTaskResponse> CancelTaskAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) =>
        PostEmptyAsync<WorkTaskResponse>($"{WorkspacePath(workspaceId)}/tasks/{taskId:D}/cancel", cancellationToken);

    public async Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkTaskActivity[]>(httpClient, $"{WorkspacePath(workspaceId)}/tasks/{taskId:D}/activities", cancellationToken);

    public async Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkTaskResult[]>(httpClient, $"{WorkspacePath(workspaceId)}/tasks/{taskId:D}/results", cancellationToken);

    public async Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<WorkTaskArtifact[]>(httpClient, $"{WorkspacePath(workspaceId)}/tasks/{taskId:D}/artifacts", cancellationToken);

    public Uri GetArtifactContentUri(Guid workspaceId, Guid taskId, Guid artifactId) =>
        new(httpClient.BaseAddress!, $"{WorkspacePath(workspaceId)}/tasks/{taskId:D}/artifacts/{artifactId:D}/content");

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
            ?? throw new AgentstrationApiException($"Work API returned an empty {typeof(TResponse).Name}.", Guid.NewGuid().ToString("N"));
    }

    private async Task<TResponse> PostEmptyAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(path, null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
            ?? throw new AgentstrationApiException($"Work API returned an empty {typeof(TResponse).Name}.", Guid.NewGuid().ToString("N"));
    }

    private static string WorkspacePath(Guid workspaceId) => $"api/workspaces/{workspaceId:D}";
}
