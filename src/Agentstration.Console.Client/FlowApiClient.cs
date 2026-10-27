using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class FlowApiClient(HttpClient httpClient) : IFlowApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken)
        => await GetFlowsCoreAsync(null, cancellationToken);

    public async Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
        => await GetFlowsCoreAsync(contextScope, cancellationToken);

    private async Task<IReadOnlyList<FlowSummary>> GetFlowsCoreAsync(ResourceScopeRef? contextScope, CancellationToken cancellationToken)
    {
        var page = contextScope is { } scope
            ? await ApiResponse.ReadAsync<FlowPageResponse>(httpClient, "api/flows?allNamespaces=true&top=100", scope, cancellationToken)
            : await ApiResponse.ReadAsync<FlowPageResponse>(httpClient, "api/flows?allNamespaces=true&top=100", cancellationToken);
        return page.Value.Select(item => new FlowSummary(item.Id, item.DisplayName ?? item.Name, item.FlowKind.ToString(), item.ActiveVersion ?? item.Version, item.Enabled ? "Active" : "Disabled", 0, 0, item.UpdatedAt) { Namespace = item.Namespace, ScopeRef = item.ScopeRef, ActiveVersion = item.ActiveVersion }).ToArray();
    }

    public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) =>
        GetFlowAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public Task<FlowResponse> GetFlowAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowResponse>(httpClient, FlowPath(@namespace, flowId), cancellationToken);

    public Task<FlowResponse> GetFlowAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowResponse>(httpClient, FlowPath(@namespace, flowId, scopeRef), cancellationToken);

    public async Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/flows/{Uri.EscapeDataString(flowId)}", cancellationToken);
        return await ReadFlowAsync(response, cancellationToken);
    }

    public async Task<FlowResourceSnapshot> GetFlowSnapshotAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(FlowPath(@namespace, flowId, scopeRef), cancellationToken);
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

    public async Task<FlowResourceSnapshot> UpdateFlowAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, UpdateFlowRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, FlowPath(@namespace, flowId, scopeRef))
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

    public async Task<FlowVersionResponse> CreateFlowVersionAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, CreateFlowVersionRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{FlowPath(@namespace, flowId)}/versions{ScopeQuery(scopeRef)}", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty published version.", Guid.NewGuid().ToString("N"));
    }

    public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) =>
        GetFlowVersionsAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public async Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowVersionResponse[]>(httpClient, $"{FlowPath(@namespace, flowId)}/versions", cancellationToken);

    public async Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowVersionResponse[]>(httpClient, $"{FlowPath(@namespace, flowId)}/versions{ScopeQuery(scopeRef)}", cancellationToken);

    public Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId, string version, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowVersionResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/versions/{Uri.EscapeDataString(version)}", cancellationToken);

    public Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId, string version, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowVersionResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/versions/{Uri.EscapeDataString(version)}{ScopeQuery(scopeRef)}", cancellationToken);

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken)
    {
        var path = flowId is null ? "api/flowRuns?top=200" : $"api/flows/{Uri.EscapeDataString(flowId)}/runs?top=200";
        return await GetAllFlowRunPagesAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(
        string? flowId,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var scope = Uri.EscapeDataString(scopeRef.Value);
        var path = flowId is null
            ? $"api/flowRuns?top=200&scopeRef={scope}"
            : $"api/flows/{Uri.EscapeDataString(flowId)}/runs?top=200&scopeRef={scope}";
        return await GetAllFlowRunPagesAsync(path, scopeRef, cancellationToken);
    }

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        await GetAllFlowRunPagesAsync($"{FlowPath(@namespace, flowId)}/runs?top=200", cancellationToken);

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef? definitionScopeRef, CancellationToken cancellationToken) =>
        (await GetFlowRunsAsync(@namespace, flowId, cancellationToken))
            .Where(run => definitionScopeRef is null || run.DefinitionScopeRef == definitionScopeRef)
            .ToArray();

    public async Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(
        ResourceNamespace @namespace,
        string flowId,
        ResourceScopeRef? definitionScopeRef,
        ResourceScopeRef executionScopeRef,
        CancellationToken cancellationToken) =>
        (await GetAllFlowRunPagesAsync($"{FlowPath(@namespace, flowId)}/runs?top=200", executionScopeRef, cancellationToken))
            .Where(run => definitionScopeRef is null || run.DefinitionScopeRef == definitionScopeRef)
            .ToArray();

    private async Task<IReadOnlyList<FlowRun>> GetAllFlowRunPagesAsync(string initialPath, CancellationToken cancellationToken)
        => await GetAllFlowRunPagesAsync(initialPath, null, cancellationToken);

    private async Task<IReadOnlyList<FlowRun>> GetAllFlowRunPagesAsync(
        string initialPath,
        ResourceScopeRef? contextScope,
        CancellationToken cancellationToken)
    {
        var runs = new List<FlowRun>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? path = initialPath;
        while (path is not null)
        {
            path = NormalizeFlowRunPageLink(path);
            if (!visited.Add(path))
                throw new AgentstrationApiException("Flow API returned a repeated pagination link.", Guid.NewGuid().ToString("N"));
            var page = contextScope is null
                ? await ApiResponse.ReadAsync<FlowRunPageResponse>(httpClient, path, cancellationToken)
                : await ApiResponse.ReadAsync<FlowRunPageResponse>(httpClient, path, contextScope.Value, cancellationToken);
            runs.AddRange(page.Value);
            path = string.IsNullOrWhiteSpace(page.NextLink) ? null : page.NextLink;
        }
        return runs;
    }

    private static string NormalizeFlowRunPageLink(string link)
    {
        var candidate = link.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            throw new AgentstrationApiException("Flow API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        var normalized = candidate.TrimStart('/');
        if (!normalized.StartsWith("api/", StringComparison.Ordinal))
            throw new AgentstrationApiException("Flow API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        return normalized;
    }

    public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowRun>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}", cancellationToken);

    public Task<FlowRun> GetFlowRunAsync(string runId, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowRun>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}{ScopeQuery(scopeRef)}", cancellationToken);

    public Task<FlowRunCausalityPageResponse> GetFlowRunCausalityAsync(string runId, CancellationToken cancellationToken) =>
        GetFlowRunCausalityCoreAsync(runId, null, cancellationToken);

    public Task<FlowRunCausalityPageResponse> GetFlowRunCausalityAsync(string runId, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        GetFlowRunCausalityCoreAsync(runId, scopeRef, cancellationToken);

    private async Task<FlowRunCausalityPageResponse> GetFlowRunCausalityCoreAsync(string runId, ResourceScopeRef? scopeRef, CancellationToken cancellationToken)
    {
        var values = new List<FlowRunCausalityNode>();
        FlowRunCausalityOrigin? origin = null;
        var totalCount = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? path = $"api/flowRuns/{Uri.EscapeDataString(runId)}/causality?top=100{ScopeParameter(scopeRef)}";
        while (path is not null)
        {
            path = NormalizeFlowRunPageLink(path);
            if (!visited.Add(path))
                throw new AgentstrationApiException("Flow causality API returned a repeated pagination link.", Guid.NewGuid().ToString("N"));
            var page = await ApiResponse.ReadAsync<FlowRunCausalityPageResponse>(httpClient, path, cancellationToken);
            origin ??= page.Origin;
            totalCount = page.TotalCount;
            values.AddRange(page.Value);
            path = string.IsNullOrWhiteSpace(page.NextLink) ? null : page.NextLink;
        }
        return new FlowRunCausalityPageResponse(
            origin ?? throw new AgentstrationApiException("Flow causality API returned no invocation origin.", Guid.NewGuid().ToString("N")),
            values,
            totalCount,
            null);
    }

    public async Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowRunEvent[]>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}/eventHistory?afterSequence={Math.Max(0, afterSequence)}", cancellationToken);

    public async Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, ResourceScopeRef scopeRef, long afterSequence, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<FlowRunEvent[]>(httpClient, $"api/flowRuns/{Uri.EscapeDataString(runId)}/eventHistory?afterSequence={Math.Max(0, afterSequence)}{ScopeParameter(scopeRef)}", scopeRef, cancellationToken);

    public async Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<InputRequest[]>(httpClient,
            $"api/flowRuns/{Uri.EscapeDataString(runId)}/inputs?status={InputRequestStatus.Pending}", cancellationToken);

    public async Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<InputRequest[]>(httpClient,
            $"api/flowRuns/{Uri.EscapeDataString(runId)}/inputs?status={InputRequestStatus.Pending}{ScopeParameter(scopeRef)}", cancellationToken);

    public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken) =>
        RespondToFlowRunInputCoreAsync(runId, inputId, null, value, cancellationToken);

    public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, ResourceScopeRef scopeRef, JsonElement value, CancellationToken cancellationToken) =>
        RespondToFlowRunInputCoreAsync(runId, inputId, scopeRef, value, cancellationToken);

    private async Task<InputRequest> RespondToFlowRunInputCoreAsync(string runId, string inputId, ResourceScopeRef? scopeRef, JsonElement value, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/flowRuns/{Uri.EscapeDataString(runId)}/inputs/{Uri.EscapeDataString(inputId)}/response{ScopeQuery(scopeRef)}",
            new SubmitInputResponseRequest(value), JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InputRequest>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Input Request.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
        => await CreateFlowRunAsync(ResourceNamespace.Default, flowId, request, cancellationToken);

    public async Task<FlowRun> CreateFlowRunAsync(ResourceNamespace @namespace, string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{FlowPath(@namespace, flowId)}/runs")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.Add("X-Agentstration-Origin", "Console");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Run.", Guid.NewGuid().ToString("N"));
    }

    private static string FlowPath(ResourceNamespace @namespace, string flowId, ResourceScopeRef? scopeRef = null)
    {
        var path = @namespace.IsDefault
            ? $"api/flows/{Uri.EscapeDataString(flowId)}"
            : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/flows/{Uri.EscapeDataString(flowId)}";
        return scopeRef is null ? path : $"{path}{ScopeQuery(scopeRef)}";
    }

    private static string ScopeQuery(ResourceScopeRef? scopeRef) =>
        scopeRef is null ? string.Empty : $"?scopeRef={Uri.EscapeDataString(scopeRef.Value.Value)}";

    private static string ScopeParameter(ResourceScopeRef? scopeRef) =>
        scopeRef is null ? string.Empty : $"&scopeRef={Uri.EscapeDataString(scopeRef.Value.Value)}";

    public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        CancelFlowRunCoreAsync(runId, null, cancellationToken);

    public Task<FlowRun> CancelFlowRunAsync(string runId, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        CancelFlowRunCoreAsync(runId, scopeRef, cancellationToken);

    private async Task<FlowRun> CancelFlowRunCoreAsync(string runId, ResourceScopeRef? scopeRef, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/flowRuns/{Uri.EscapeDataString(runId)}/cancel{ScopeQuery(scopeRef)}", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Run.", Guid.NewGuid().ToString("N"));
    }

    public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var run in ObserveFlowRunCoreAsync(runId, null, cancellationToken)) yield return run;
    }

    public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, ResourceScopeRef scopeRef, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var run in ObserveFlowRunCoreAsync(runId, scopeRef, cancellationToken)) yield return run;
    }

    private async IAsyncEnumerable<FlowRun> ObserveFlowRunCoreAsync(string runId, ResourceScopeRef? scopeRef, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/flowRuns/{Uri.EscapeDataString(runId)}/events{ScopeQuery(scopeRef)}");
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
        GetDraftAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public Task<FlowDraftResponse> GetDraftAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowDraftResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/draft", cancellationToken);

    public Task<FlowDraftResponse> GetDraftAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowDraftResponse>(httpClient, DraftPath(@namespace, flowId, scopeRef), cancellationToken);

    public async Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
        => await SaveDraftAsync(ResourceNamespace.Default, flowId, request, etag, cancellationToken);

    public async Task<FlowDraftResponse> SaveDraftAsync(ResourceNamespace @namespace, string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{FlowPath(@namespace, flowId)}/draft") { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowDraftResponse> SaveDraftAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, DraftPath(@namespace, flowId, scopeRef)) { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken)
        => await ValidateDraftAsync(ResourceNamespace.Default, flowId, cancellationToken);

    public async Task<FlowValidationResponse> ValidateDraftAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{FlowPath(@namespace, flowId)}/validate", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowValidationResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty validation result.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowValidationResponse> ValidateDraftAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(DraftChildPath(@namespace, flowId, "validate", scopeRef), null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowValidationResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty validation result.", Guid.NewGuid().ToString("N"));
    }

    public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) =>
        GetDraftSourceAsync(ResourceNamespace.Default, flowId, format, cancellationToken);

    public Task<FlowSourceResponse> GetDraftSourceAsync(ResourceNamespace @namespace, string flowId, string format, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowSourceResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/draft/source?format={Uri.EscapeDataString(format)}", cancellationToken);

    public Task<FlowSourceResponse> GetDraftSourceAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, string format, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<FlowSourceResponse>(httpClient, $"{FlowPath(@namespace, flowId)}/draft/source?scopeRef={Uri.EscapeDataString(scopeRef.Value)}&format={Uri.EscapeDataString(format)}", cancellationToken);

    public async Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
        => await ReplaceDraftSourceAsync(ResourceNamespace.Default, flowId, request, etag, cancellationToken);

    public async Task<FlowDraftResponse> ReplaceDraftSourceAsync(ResourceNamespace @namespace, string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{FlowPath(@namespace, flowId)}/draft/source") { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowDraftResponse> ReplaceDraftSourceAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, DraftChildPath(@namespace, flowId, "draft/source", scopeRef)) { Content = JsonContent.Create(request, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken)
        => await PublishDraftAsync(ResourceNamespace.Default, flowId, request, cancellationToken);

    public async Task<FlowVersionResponse> PublishDraftAsync(ResourceNamespace @namespace, string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{FlowPath(@namespace, flowId)}/publish", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty published version.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowVersionResponse> PublishDraftAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, PublishFlowDraftRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(DraftChildPath(@namespace, flowId, "publish", scopeRef), request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty published version.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
        => await CreateDraftRunAsync(ResourceNamespace.Default, flowId, request, cancellationToken);

    public async Task<FlowRun> CreateDraftRunAsync(ResourceNamespace @namespace, string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"{FlowPath(@namespace, flowId)}/draft/runs", request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Draft Run.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowRun> CreateDraftRunAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(DraftChildPath(@namespace, flowId, "draft/runs", scopeRef), request, JsonOptions, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRun>(JsonOptions, cancellationToken)
            ?? throw new AgentstrationApiException("Flow API returned an empty Draft Run.", Guid.NewGuid().ToString("N"));
    }

    public async Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken)
        => await CreateDraftFromVersionAsync(ResourceNamespace.Default, flowId, version, cancellationToken);

    public async Task<FlowDraftResponse> CreateDraftFromVersionAsync(ResourceNamespace @namespace, string flowId, string version, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{FlowPath(@namespace, flowId)}/versions/{Uri.EscapeDataString(version)}/draft", null, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    public async Task<FlowDraftResponse> CreateDraftFromVersionAsync(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef, string version, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(DraftChildPath(@namespace, flowId, $"versions/{Uri.EscapeDataString(version)}/draft", scopeRef), null, cancellationToken);
        return await ReadDraftAsync(response, cancellationToken);
    }

    private static string DraftPath(ResourceNamespace @namespace, string flowId, ResourceScopeRef scopeRef) =>
        $"{FlowPath(@namespace, flowId)}/draft{ScopeQuery(scopeRef)}";

    private static string DraftChildPath(ResourceNamespace @namespace, string flowId, string child, ResourceScopeRef scopeRef) =>
        $"{FlowPath(@namespace, flowId)}/{child}{ScopeQuery(scopeRef)}";

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
