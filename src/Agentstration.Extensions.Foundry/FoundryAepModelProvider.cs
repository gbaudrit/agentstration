using System.Net;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryAepModelProvider(
    HttpClient httpClient,
    FoundryExtensionOptions options,
    FoundryRequestAuthenticator authenticator,
    ILogger<FoundryAepModelProvider>? logger = null) : IAepModelProvider
{
    private readonly ILogger diagnosticsLogger = logger ?? NullLogger<FoundryAepModelProvider>.Instance;
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "microsoft-foundry",
        "Microsoft Foundry",
        new AepModelProviderCapabilities(
            Chat: true,
            Streaming: true,
            Tools: true,
            Thinking: true,
            StructuredOutput: true,
            Vision: false,
            ModelDiscovery: true));

    public async Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken)
    {
        using var diagnostics = new FoundryDiagnostics(diagnosticsLogger, "chat", request?.Model);
        try
        {
            _ = FoundryChatCompletion.BuildRequest(request!);
            await ValidateAdvancedCapabilitiesAsync(request!, cancellationToken);
            var response = await FoundryChatCompletion.ExecuteAsync(httpClient, options, authenticator, request!, diagnostics, cancellationToken);
            diagnostics.SetUsage(response.Usage);
            diagnostics.Complete("success");
            return response;
        }
        catch (AepServerException exception) { diagnostics.Complete(exception.Code); throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { diagnostics.Complete("cancelled"); throw; }
    }

    public IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(
        AepChatRequest request,
        CancellationToken cancellationToken) =>
        ChatStreamingValidatedAsync(request, cancellationToken);

    private async IAsyncEnumerable<AepChatUpdate> ChatStreamingValidatedAsync(
        AepChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var diagnostics = new FoundryDiagnostics(diagnosticsLogger, "chat_stream", request?.Model);
        _ = FoundryChatCompletion.BuildRequest(request!, streaming: true);
        await ValidateAdvancedCapabilitiesAsync(request!, cancellationToken);
        await using var updates = FoundryChatStream.ExecuteAsync(httpClient, options, authenticator, request!, diagnostics, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            AepChatUpdate update;
            try
            {
                if (!await updates.MoveNextAsync()) break;
                update = updates.Current;
            }
            catch (AepServerException exception) { diagnostics.Complete(exception.Code); throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { diagnostics.Complete("cancelled"); throw; }
            diagnostics.SetUsage(update.Usage);
            yield return update;
        }
        diagnostics.Complete("success");
    }

    private async Task ValidateAdvancedCapabilitiesAsync(AepChatRequest request, CancellationToken cancellationToken)
    {
        var format = FoundryChatCompletion.RequestedFormat(request.Options);
        var effort = FoundryChatCompletion.RequestedReasoningEffort(request.Options);
        if ((format is null or "text") && effort is null) return;
        var model = (await ListModelsAsync(cancellationToken)).SingleOrDefault(value => value.Id == request.Model);
        if (model is null) throw new AepServerException("model_unavailable", "The Foundry deployment is not available.", 400);
        if (format is "json_object" or "json_schema"
            && !model.Metadata!.ContainsKey(format == "json_object" ? "jsonObject" : "jsonSchema"))
            throw new AepServerException("unsupported_option", "The Foundry deployment does not advertise the requested output format.", 400);
        if (effort is not null)
        {
            if (!model.Capabilities!.Contains("reasoning", StringComparer.Ordinal)
                || effort != "default" && (model.Metadata is null
                    || !model.Metadata.TryGetValue("reasoningEfforts", out var efforts)
                    || !efforts.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(effort, StringComparer.Ordinal)))
                throw new AepServerException("unsupported_option", "The Foundry deployment does not advertise the requested reasoning effort.", 400);
        }
    }

    public async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var diagnostics = new FoundryDiagnostics(diagnosticsLogger, "discovery", null);
        try
        {
            var result = await ListModelsCoreAsync(diagnostics, cancellationToken);
            diagnostics.Complete("success");
            return result;
        }
        catch (AepServerException exception) { diagnostics.Complete(exception.Code); throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { diagnostics.Complete("cancelled"); throw; }
    }

    private async Task<IReadOnlyList<AepModelDescriptor>> ListModelsCoreAsync(FoundryDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var models = new Dictionary<string, AepModelDescriptor>(StringComparer.Ordinal);
        var current = options.DeploymentsEndpoint();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            if (page >= options.MaximumDiscoveryPages)
                throw new AepServerException("discovery_limit", "Foundry deployment discovery exceeded its page limit.");
            ValidatePageUri(current);
            if (!visited.Add(current.AbsoluteUri))
                throw new AepServerException("discovery_invalid", "Foundry deployment pagination contains a cycle.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await SendDiscoveryPageAsync(current, diagnostics, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AepServerException("provider_timeout", "Foundry deployment discovery timed out.");
            }
            catch (HttpRequestException exception)
            {
                throw new AepServerException("provider_unavailable", "Foundry deployment discovery is unavailable.", innerException: exception);
            }
            using (response)
            {
                diagnostics.SetStatus(response.StatusCode);
                EnsureSuccess(response);
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new AepServerException("discovery_invalid", "Foundry deployment discovery requires a JSON response.");
                byte[] bytes;
                try { bytes = await ReadBoundedAsync(response.Content, options.MaximumDiscoveryResponseBytes, timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AepServerException("provider_timeout", "Foundry deployment discovery timed out.");
                }
                catch (HttpRequestException exception)
                {
                    throw new AepServerException("provider_unavailable", "Foundry deployment discovery is unavailable.", innerException: exception);
                }
                catch (IOException exception)
                {
                    throw new AepServerException("provider_unavailable", "Foundry deployment discovery was interrupted.", innerException: exception);
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException exception)
                {
                    throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment document.", innerException: exception);
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var values)
                        || values.ValueKind != JsonValueKind.Array)
                        throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment list.");
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != "ModelDeployment") continue;
                        var name = GetString(item, "name");
                        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) continue;
                        if (!HasChatCapability(item)) continue;
                        if (models.Count >= options.MaximumDiscoveredModels)
                            throw new AepServerException("discovery_limit", "Foundry deployment discovery exceeded its model limit.");
                        if (models.ContainsKey(name))
                            throw new AepServerException("discovery_invalid", "Foundry returned a duplicate deployment name.");
                        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                        AddMetadata(item, metadata, "modelPublisher", "publisher");
                        AddMetadata(item, metadata, "modelName", "model");
                        AddMetadata(item, metadata, "modelVersion", "version");
                        var capabilities = new List<string> { "chat", "streaming" };
                        if (HasTrueCapability(item, "toolCalling", "tool_calls", "functionCalling"))
                            capabilities.Add("tools");
                        if (HasTrueCapability(item, "jsonObject", "json_object")) metadata["jsonObject"] = "true";
                        if (HasTrueCapability(item, "jsonSchema", "json_schema")) metadata["jsonSchema"] = "true";
                        if (metadata.ContainsKey("jsonObject") || metadata.ContainsKey("jsonSchema")) capabilities.Add("structuredOutput");
                        if (HasTrueCapability(item, "reasoning"))
                        {
                            capabilities.Add("reasoning");
                            AddReasoningEfforts(item, metadata);
                        }
                        models.Add(name, new AepModelDescriptor(name, name, capabilities, metadata));
                    }
                    if (!root.TryGetProperty("nextLink", out var nextLink) || nextLink.ValueKind == JsonValueKind.Null)
                        return models.Values.ToArray();
                    if (nextLink.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nextLink.GetString())
                        || !Uri.TryCreate(current, nextLink.GetString(), out var next))
                        throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment continuation.");
                    current = next;
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendDiscoveryPageAsync(Uri endpoint, FoundryDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        // Discovery is a read-only request. A chat POST, including a stream, is never replayed.
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            await authenticator.ApplyAsync(request, cancellationToken);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                diagnostics.Retried();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }
            if (attempt > 0 || response.StatusCode is not (HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                return response;
            var delay = response.Headers.RetryAfter?.Delta;
            diagnostics.SetStatus(response.StatusCode);
            diagnostics.Retried();
            response.Dispose();
            await Task.Delay(delay is { } retryAfter && retryAfter >= TimeSpan.Zero && retryAfter <= TimeSpan.FromMilliseconds(500)
                ? retryAfter : TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ListModelsAsync(cancellationToken);
            return new AepProviderHealth("available");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AepServerException exception) { return new AepProviderHealth("unavailable", exception.Code); }
    }

    private void ValidatePageUri(Uri uri)
    {
        var first = options.DeploymentsEndpoint();
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.IdnHost, first.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != first.Port || uri.AbsolutePath != first.AbsolutePath
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 2048)
            throw new AepServerException("discovery_origin_invalid", "Foundry deployment continuation must remain on the configured endpoint.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new AepServerException("discovery_limit", "Foundry deployment response exceeded its size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new AepServerException("discovery_limit", "Foundry deployment response exceeded its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new AepServerException("provider_redirect_denied", "Foundry redirected a credential-bearing request.");
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication_failed",
            HttpStatusCode.Forbidden => "authorization_failed",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.NotFound => "project_unavailable",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "provider_timeout",
            _ => "provider_unavailable"
        };
        throw new AepServerException(code, $"Foundry deployment discovery returned HTTP {(int)response.StatusCode}.");
    }

    private static bool HasChatCapability(JsonElement item) =>
        HasTrueCapability(item, "chat", "chatCompletion", "chatCompletions", "chat_completion", "chat_completions");

    private static bool HasTrueCapability(JsonElement item, params string[] names)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object) return false;
        foreach (var capability in capabilities.EnumerateObject())
        {
            if (!names.Contains(capability.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (capability.Value.ValueKind == JsonValueKind.True
                || capability.Value.ValueKind == JsonValueKind.String && string.Equals(capability.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AddReasoningEfforts(JsonElement item, IDictionary<string, string> metadata)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities)
            || !capabilities.TryGetProperty("reasoningEfforts", out var efforts)
            || efforts.ValueKind != JsonValueKind.Array || efforts.GetArrayLength() > 8) return;
        var allowed = new HashSet<string>(["none", "minimal", "low", "medium", "high"], StringComparer.Ordinal);
        var values = efforts.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!).Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length > 0) metadata["reasoningEfforts"] = string.Join(',', values);
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddMetadata(JsonElement item, IDictionary<string, string> metadata, string property, string key)
    {
        var value = GetString(item, property);
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl))
            metadata[key] = value;
    }
}
