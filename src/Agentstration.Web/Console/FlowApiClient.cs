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

public sealed class FlowApiClient(HttpClient httpClient) : IFlowApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken)
    {
        var page = await ApiResponse.ReadAsync<FlowPageResponse>(httpClient, "api/flows?allNamespaces=true&top=100", cancellationToken);
        return page.Value.Select(item => new FlowSummary(item.Id, item.Name, item.FlowKind.ToString(), item.ActiveVersion ?? item.Version, item.Enabled ? "Active" : "Disabled", 0, 0, item.UpdatedAt) { Namespace = item.Namespace }).ToArray();
    }

    public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) =>
        GetFlowAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public Task<FlowResponse> GetFlowAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowResponse>(httpClient, FlowPath(@namespace, flowId), cancellationToken);

    public async Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/flows/{Uri.EscapeDataString(flowId)}", cancellationToken);
        return await ReadFlowAsync(response, cancellationToken);
    }

    public async Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/flows", request, JsonOptions, cancellationToken);
        return await ReadFlowAsync(response, cancellationToken);
    }

    public async Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"api/flows/{Uri.EscapeDataString(flowId)}")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadFlowAsync(response, cancellationToken);
    }

    public async Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/flows/{Uri.EscapeDataString(flowId)}/versions", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty published version.", Guid.NewGuid().ToString("N"));
    }

    public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) =>
        GetFlowVersionsAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public async Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowVersionResponse[]>(httpClient, $"{FlowPath(@namespace, flowId)}/versions", cancellationToken);

    public Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId, string version, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowVersionResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/versions/{Uri.EscapeDataString(version)}", cancellationToken);

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken)
    {
        var path = flowId is null ? "api/flowRuns?top=200" : $"api/flows/{Uri.EscapeDataString(flowId)}/runs?top=200";
        return await GetAllFlowRunPagesAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        await GetAllFlowRunPagesAsync($"{FlowPath(@namespace, flowId)}/runs?top=200", cancellationToken);

    private async Task<IReadOnlyList<FlowRun>> GetAllFlowRunPagesAsync(string initialPath, CancellationToken cancellationToken)
    {
        var runs = new List<FlowRun>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? path = initialPath;
        while (path is not null)
        {
            path = NormalizeFlowRunPageLink(path);
            if (!visited.Add(path))
                throw new AgentstrationApiException("Flow API returned a repeated pagination link.", Guid.NewGuid().ToString("N"));
            var page = await ApiResponse.ReadAsync<FlowRunPageResponse>(httpClient, path, cancellationToken);
            runs.AddRange(page.Value);
            path = string.IsNullOrWhiteSpace(page.NextLink) ? null : page.NextLink;
        }
        return runs;
    }

    private static string NormalizeFlowRunPageLink(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out _))
            throw new AgentstrationApiException("Flow API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        var normalized = link.TrimStart('/');
        if (!normalized.StartsWith("api/", StringComparison.Ordinal))
            throw new AgentstrationApiException("Flow API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        return normalized;
    }

    public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowRun>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}", cancellationToken);

    public async Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowRunEvent[]>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}/eventHistory?afterSequence={Math.Max(0, afterSequence)}", cancellationToken);

    public async Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<InputRequest[]>(httpClient,
            $"api/flowRuns/{Uri.EscapeDataString(runId)}/inputs?status={InputRequestStatus.Pending}", cancellationToken);

    public async Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/flowRuns/{Uri.EscapeDataString(runId)}/inputs/{Uri.EscapeDataString(inputId)}/response",
            new SubmitInputResponseRequest(value), JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InputRequest>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Input Request.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
        => await CreateFlowRunAsync(ResourceNamespace.Default, flowId, request, cancellationToken);

    public async Task<FlowRun> CreateFlowRunAsync(ResourceNamespace @namespace, string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{FlowPath(@namespace, flowId)}/runs", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Run.", Guid.NewGuid().ToString("N"));
    }

    private static string FlowPath(ResourceNamespace @namespace, string flowId) => @namespace.IsDefault
        ? $"api/flows/{Uri.EscapeDataString(flowId)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/flows/{Uri.EscapeDataString(flowId)}";

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

    private static async Task<FlowResourceSnapshot> ReadFlowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty definition.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.Tag
            ?? throw new AgentstrationApiException("Flow API returned no ETag.", Guid.NewGuid().ToString("N"));
        return new FlowResourceSnapshot(value, etag);
    }
}

