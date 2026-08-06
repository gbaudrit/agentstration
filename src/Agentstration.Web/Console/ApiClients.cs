using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Flow.Contracts;
using Agentstration.Flow;
using Agentstration.Management.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work.Contracts;
using Agentstration.Work;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;

namespace Agentstration.Web.Console;

public interface IManagementApiClient
{
    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(string resourceGroup, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentTypeResource>> GetAgentTypesAsync(string resourceGroup, CancellationToken cancellationToken);
    Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string resourceGroup, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken);
    Task DeleteAgentAsync(string resourceGroup, string name, string etag, CancellationToken cancellationToken);
    Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public interface IAgentRunnerManagementClient
{
    Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string resourceGroup, string name, CancellationToken cancellationToken);
}

public interface IRuntimeApiClient
{
    Task<IReadOnlyList<RuntimeInstanceSummary>> GetInstancesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken);
    Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken);
    Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, CancellationToken cancellationToken);
    Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken);
    Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken);
}

public interface IAgentRunnerRuntimeClient : IRuntimeApiClient
{
    Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(string resourceGroup, string agentName, long generation, CancellationToken cancellationToken);
    Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(string resourceGroup, string agentName, long generation, CancellationToken cancellationToken);
}

public interface IWorkApiClient
{
    Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken);
    Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken);
    Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken);
    Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken);
    Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken);
}

public interface IFlowApiClient
{
    Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken);
    Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken);
    Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken);
    Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken);
    Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken);
    IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, CancellationToken cancellationToken);
    Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken);
    Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken);
    Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken);
    Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken);
}

public interface IAgentstrationEventStream
{
    Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<EventListItem> SubscribeAsync(CancellationToken cancellationToken);
}

public sealed class ManagementApiClient(HttpClient httpClient) : IManagementApiClient, IAgentRunnerManagementClient
{
    public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) =>
        GetAgentsAsync("default", cancellationToken);

    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(string resourceGroup, CancellationToken cancellationToken)
    {
        var path = $"resourceGroups/{Uri.EscapeDataString(resourceGroup)}/providers/{AgentstrationProviderNamespaces.Agents}/agents?api-version={ManagementApiVersions.V20260801}";
        var page = await ApiResponse.ReadAsync<PagedResponse<AgentResource>>(httpClient, path, cancellationToken);
        return page.Value.Select(agent => new AgentSummary(agent.Id, agent.Properties.DisplayName, agent.Properties.AgentType.ResourceId, agent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), agent.Status.ProvisioningState.ToString(), agent.Properties.Tools.Select(tool => tool.ResourceId).ToArray(), "Not deployed", DateTimeOffset.MinValue, agent.Properties.ModelProfile.ResourceId)).ToArray();
    }

    public async Task<IReadOnlyList<AgentTypeResource>> GetAgentTypesAsync(string resourceGroup, CancellationToken cancellationToken)
    {
        var path = $"resourceGroups/{Uri.EscapeDataString(resourceGroup)}/providers/{AgentstrationProviderNamespaces.Agents}/agentTypes?api-version={ManagementApiVersions.V20260801}";
        var page = await ApiResponse.ReadAsync<PagedResponse<AgentTypeResource>>(httpClient, path, cancellationToken);
        return page.Value;
    }

    public async Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string resourceGroup, string name, CancellationToken cancellationToken)
    {
        var path = AgentPath(resourceGroup, name);
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadResourceAsync<AgentResource>(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, AgentPath(request.ResourceGroup, request.Name))
        {
            Content = JsonContent.Create(request)
        };
        if (createOnly) message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync<AgentResource>(response, cancellationToken);
    }

    public async Task DeleteAgentAsync(string resourceGroup, string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, AgentPath(resourceGroup, name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var agentsTask = GetAgentsAsync(cancellationToken);
        var typesTask = GetAgentTypesAsync("default", cancellationToken);
        await Task.WhenAll(agentsTask, typesTask);
        var agents = await agentsTask;
        return new((await typesTask).Count, agents.Count, agents.Sum(item => int.TryParse(item.Version, out var version) ? version : 0), 0, "Managed");
    }

    private static string AgentPath(string resourceGroup, string name) =>
        $"resourceGroups/{Uri.EscapeDataString(resourceGroup)}/providers/{AgentstrationProviderNamespaces.Agents}/agents/{Uri.EscapeDataString(name)}?api-version={ManagementApiVersions.V20260801}";

    private static async Task<ResourceSnapshot<T>> ReadResourceAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the resource ETag.", Guid.NewGuid().ToString("N"));
        return new ResourceSnapshot<T>(value, etag);
    }
}

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
    public async Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => await ApiResponse.ReadAsync<WorkplaceWorkspaceResponse[]>(httpClient, "api/workspaces", cancellationToken);
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

public sealed class FlowApiClient(HttpClient httpClient) : IFlowApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken)
    {
        var page = await ApiResponse.ReadAsync<FlowPageResponse>(httpClient, "api/flows?top=100", cancellationToken);
        return page.Value.Select(item => new FlowSummary(item.Id, item.Name, item.Kind.ToString(), item.ActiveVersion ?? item.Version, item.Enabled ? "Active" : "Disabled", 0, 0, item.UpdatedAt)).ToArray();
    }

    public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowResponse>(httpClient, $"api/flows/{Uri.EscapeDataString(flowId)}", cancellationToken);

    public async Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowVersionResponse[]>(httpClient, $"api/flows/{Uri.EscapeDataString(flowId)}/versions", cancellationToken);

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken)
    {
        var path = flowId is null ? "api/flowRuns?top=200" : $"api/flows/{Uri.EscapeDataString(flowId)}/runs?top=200";
        return (await ApiResponse.ReadAsync<FlowRunPageResponse>(httpClient, path, cancellationToken)).Value;
    }

    public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowRun>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}", cancellationToken);

    public async Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowRunEvent[]>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}/eventHistory?afterSequence={Math.Max(0, afterSequence)}", cancellationToken);

    public async Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/flows/{Uri.EscapeDataString(flowId)}/runs", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Run.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/flowRuns/{Uri.EscapeDataString(runId)}/cancel", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Run.", Guid.NewGuid().ToString("N"));
    }

    public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/flowRuns/{Uri.EscapeDataString(runId)}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var run = JsonSerializer.Deserialize<FlowRun>(line[5..].TrimStart(), JsonOptions);
            if (run is not null) yield return run;
        }
    }

    public async Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/flows/drafts", request, JsonOptions, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowDraftResponse>(httpClient, $"api/flows/{Uri.EscapeDataString(flowId)}/draft", cancellationToken);

    public async Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"api/flows/{Uri.EscapeDataString(flowId)}/draft") { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/flows/{Uri.EscapeDataString(flowId)}/validate", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowValidationResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty validation result.", Guid.NewGuid().ToString("N"));
    }

    public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowSourceResponse>(httpClient, $"api/flows/{Uri.EscapeDataString(flowId)}/draft/source?format={Uri.EscapeDataString(format)}", cancellationToken);

    public async Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"api/flows/{Uri.EscapeDataString(flowId)}/draft/source") { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/flows/{Uri.EscapeDataString(flowId)}/publish", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty published version.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/flows/{Uri.EscapeDataString(flowId)}/draft/runs", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Draft Run.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/flows/{Uri.EscapeDataString(flowId)}/versions/{Uri.EscapeDataString(version)}/draft", null, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    private static async Task<FlowDraftResponse> ReadDraftAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Draft.", Guid.NewGuid().ToString("N"));
    }
}

public sealed class RuntimeApiClient(HttpClient httpClient) : IRuntimeApiClient, IAgentRunnerRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RuntimeInstanceSummary>> GetInstancesAsync(CancellationToken cancellationToken)
    {
        _ = await ApiResponse.ReadAsync<HealthResponse>(httpClient, "health", cancellationToken);
        return [new("local-runtime", "Shared runtime", "Ready", "InProcess", "local", "Idle", 0, 0)];
    }

    public async Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken)
    {
        var runs = await GetRunsAsync(null, cancellationToken);
        return runs.Select(run => new ExecutionSummary(
            run.Id,
            ResourceIdentifier.TryParse(run.Properties.Agent.ResourceId, out var id) ? id.Name : run.Properties.Agent.ResourceId,
            null,
            null,
            run.Status.State.ToString(),
            run.Status.StartedAt ?? run.Status.CreatedAt,
            run.Status.CompletedAt is { } completed ? completed - (run.Status.StartedAt ?? run.Status.CreatedAt) : null,
            run.Status.Response,
            run.Status.Error)).ToArray();
    }

    public async Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/runtime/runs", request, cancellationToken);
        return await ReadRunAsync(response, cancellationToken);
    }

    public Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<RuntimeRun>(httpClient, $"api/runtime/runs/{Uri.EscapeDataString(runId)}", cancellationToken);

    public async Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(agentResourceId) ? string.Empty : $"?agentResourceId={Uri.EscapeDataString(agentResourceId)}";
        var page = await ApiResponse.ReadAsync<RuntimeRunPageResponse>(httpClient, $"api/runtime/runs{query}", cancellationToken);
        return page.Value;
    }

    public async IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/runtime/runs/{Uri.EscapeDataString(runId)}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSequence > 0) request.Headers.TryAddWithoutValidation("Last-Event-ID", afterSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? data = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].TrimStart();
            if (line.Length == 0 && data is not null)
            {
                var runEvent = JsonSerializer.Deserialize<RuntimeRunEvent>(data, JsonOptions)
                    ?? throw new AgentstrationApiException("Runtime returned an invalid SSE event.", Guid.NewGuid().ToString("N"));
                data = null;
                yield return runEvent;
            }
        }
    }

    public async Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/runtime/runs/{Uri.EscapeDataString(runId)}/cancel", null, cancellationToken);
        return await ReadRunAsync(response, cancellationToken);
    }

    public async Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/runtime/runs/{Uri.EscapeDataString(runId)}/retry", null, cancellationToken);
        return await ReadRunAsync(response, cancellationToken);
    }

    public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(string resourceGroup, string agentName, long generation, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<AgentRuntimeReadinessResponse>(httpClient,
            $"api/runtime/agents/{Uri.EscapeDataString(agentName)}/readiness?resourceGroup={Uri.EscapeDataString(resourceGroup)}&generation={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);

    public async Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(string resourceGroup, string agentName, long generation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"api/runtime/agents/{Uri.EscapeDataString(agentName)}/prepare?resourceGroup={Uri.EscapeDataString(resourceGroup)}&generation={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            null,
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PrepareAgentRuntimeResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Runtime returned an empty preparation response.", Guid.NewGuid().ToString("N"));
    }

    private static async Task<RuntimeRun> ReadRunAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RuntimeRun>(cancellationToken)
            ?? throw new AgentstrationApiException("Runtime returned an empty run response.", Guid.NewGuid().ToString("N"));
    }

    private sealed record HealthResponse(string Status);
}

internal static class ApiResponse
{
    public static async Task<T> ReadAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var correlationId = response.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.FirstOrDefault() : null;
        ApiProblemDetails? problem = null;
        try { problem = await response.Content.ReadFromJsonAsync<ApiProblemDetails>(cancellationToken); }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        var message = problem?.Detail ?? problem?.Title ?? $"Agentstration API returned {(int)response.StatusCode} ({response.ReasonPhrase}).";
        throw new AgentstrationApiException(message, correlationId ?? Guid.NewGuid().ToString("N"), response.StatusCode, problem?.Title);
    }

    private sealed record ApiProblemDetails(string? Title, string? Detail, int? Status);
}

public sealed class AgentstrationApiException(string message, string errorId, HttpStatusCode? statusCode = null, string? problemTitle = null) : Exception(message)
{
    public string ErrorId { get; } = errorId;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProblemTitle { get; } = problemTitle;
    public bool IsConcurrencyConflict => StatusCode == HttpStatusCode.PreconditionFailed
        || StatusCode == HttpStatusCode.Conflict && string.Equals(ProblemTitle, "Resource version conflict", StringComparison.OrdinalIgnoreCase);
}
