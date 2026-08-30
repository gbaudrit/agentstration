using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class WorkApiClient(HttpClient httpClient) : IWorkApiClient
{
    public async Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken)
    {
        var page = await GetTasksAsync(null, null, null, null, 1, 100, "updatedAt", "desc", cancellationToken);
        return page.Items.Select(item => new WorkSummary(item.Id, item.Title, "WorkTask", item.Status.ToString(), "—", WorkspaceName(item.WorkspaceId), item.CreatedAt, item.UpdatedAt)).ToArray();
    }

    public Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken)
    {
        var values = new List<string> { $"page={Math.Max(1, page)}", $"pageSize={Math.Clamp(pageSize, 1, 100)}", $"sort={Uri.EscapeDataString(sort)}", $"direction={Uri.EscapeDataString(direction)}" };
        if (!string.IsNullOrWhiteSpace(workspaceId)) values.Add($"workspaceId={Uri.EscapeDataString(workspaceId)}");
        if (status is not null) values.Add($"status={status}");
        if (!string.IsNullOrWhiteSpace(search)) values.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (hasPendingAction is not null) values.Add($"hasPendingAction={hasPendingAction.Value.ToString().ToLowerInvariant()}");
        return ApiResponse.ReadAsync<WorkTaskOperationsPageResponse>(httpClient, $"api/tasks?{string.Join('&', values)}", cancellationToken);
    }

    public Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<WorkTaskOperationsCountersResponse>(httpClient, string.IsNullOrWhiteSpace(workspaceId) ? "api/tasks/summary" : $"api/tasks/summary?workspaceId={Uri.EscapeDataString(workspaceId)}", cancellationToken);
    public Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => ApiResponse.ReadAsync<WorkTaskOperationsDetailResponse>(httpClient, $"api/tasks/{taskId}", cancellationToken);
    public Task<FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken) => ApiResponse.ReadAsync<FlowRun>(httpClient, $"api/tasks/{taskId}/flow-runs/{Uri.EscapeDataString(runId)}", cancellationToken);
    public async Task<PendingActionContract> RespondTaskPendingActionAsync(Guid taskId, Guid actionId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/tasks/{taskId}/pending-actions/{actionId}/respond", new TaskPendingActionResponse(values), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PendingActionContract>(cancellationToken)
            ?? throw new AgentstrationApiException("Work API returned an empty Pending Action.", Guid.NewGuid().ToString("N"));
    }
    public async Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => await ApiResponse.ReadAsync<WorkplaceWorkspaceResponse[]>(httpClient, "api/workplace/workspaces", cancellationToken);
    public Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken) => CommandAsync(taskId, "pause", cancellationToken);
    public Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken) => CommandAsync(taskId, "resume", cancellationToken);
    public Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken) => CommandAsync(taskId, "cancel", cancellationToken);

    private async Task CommandAsync(Guid taskId, string command, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/tasks/{taskId}/{command}", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
    private static string WorkspaceName(string id) => id[(id.LastIndexOf('/') + 1)..];
}

