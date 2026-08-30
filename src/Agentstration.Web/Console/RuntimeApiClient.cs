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

public sealed class RuntimeApiClient(HttpClient httpClient) : IRuntimeApiClient, IAgentRunnerRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken)
    {
        var runs = await GetRunsAsync(null, cancellationToken);
        return runs.Select(run => new ExecutionSummary(
            run.Id,
            run.Properties.Agent.ResourceId,
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

    public async Task<IReadOnlyList<RuntimeRunEvent>> GetRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<RuntimeRunEvent[]>(httpClient,
            $"api/runtime/runs/{Uri.EscapeDataString(runId)}/eventHistory?afterSequence={Math.Max(0, afterSequence)}",
            cancellationToken);

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

    public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(string agentName, long generation, CancellationToken cancellationToken) =>
        GetAgentReadinessAsync(ResourceNamespace.Default, agentName, generation, cancellationToken);

    public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<AgentRuntimeReadinessResponse>(httpClient,
            $"{RuntimeAgentPath(@namespace, agentName)}/readiness?generation={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);

    public async Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(string agentName, long generation, CancellationToken cancellationToken)
        => await PrepareAgentAsync(ResourceNamespace.Default, agentName, generation, cancellationToken);

    public async Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"{RuntimeAgentPath(@namespace, agentName)}/prepare?generation={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            null,
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PrepareAgentRuntimeResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Runtime returned an empty preparation response.", Guid.NewGuid().ToString("N"));
    }

    private static string RuntimeAgentPath(ResourceNamespace @namespace, string agentName) => @namespace.IsDefault
        ? $"api/runtime/agents/{Uri.EscapeDataString(agentName)}"
        : $"api/runtime/namespaces/{Uri.EscapeDataString(@namespace.Value)}/agents/{Uri.EscapeDataString(agentName)}";

    private static async Task<RuntimeRun> ReadRunAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RuntimeRun>(cancellationToken)
            ?? throw new AgentstrationApiException("Runtime returned an empty run response.", Guid.NewGuid().ToString("N"));
    }

}

