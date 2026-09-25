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
    FoundryBoundConnectionResolver connectionResolver,
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
            using var lease = await connectionResolver.ResolveAsync(request!.BoundValues, cancellationToken);
            var authenticator = new FoundryRequestAuthenticator(lease.Connection);
            _ = FoundryChatCompletion.BuildRequest(request!);
            await ValidateAdvancedCapabilitiesAsync(request!, lease.Connection, authenticator, cancellationToken);
            var response = await FoundryChatCompletion.ExecuteAsync(httpClient, options, lease.Connection, authenticator, request!, diagnostics, cancellationToken);
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
        using var lease = await connectionResolver.ResolveAsync(request!.BoundValues, cancellationToken);
        var authenticator = new FoundryRequestAuthenticator(lease.Connection);
        _ = FoundryChatCompletion.BuildRequest(request!, streaming: true);
        await ValidateAdvancedCapabilitiesAsync(request!, lease.Connection, authenticator, cancellationToken);
        await using var updates = FoundryChatStream.ExecuteAsync(httpClient, options, lease.Connection, authenticator, request!, diagnostics, cancellationToken)
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

    private async Task ValidateAdvancedCapabilitiesAsync(
        AepChatRequest request,
        FoundryConnection connection,
        FoundryRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var format = FoundryChatCompletion.RequestedFormat(request.Options);
        var effort = FoundryChatCompletion.RequestedReasoningEffort(request.Options);
        if ((format is null or "text") && effort is null) return;
        using var diagnostics = new FoundryDiagnostics(diagnosticsLogger, "capabilities", request.Model);
        var model = (await ListModelsCoreAsync(connection, authenticator, diagnostics, cancellationToken)).SingleOrDefault(value => value.Id == request.Model);
        if (model is null) throw new AepServerException("model_unavailable", "The Foundry deployment is not available.", 400);
        var specification = request.EffectiveSpecification ?? model.Specification;
        if (format is "json_object" or "json_schema"
            && specification?.Features.StructuredOutput?.Formats.ContainsKey(
                format == "json_object"
                    ? AepModelStructuredOutputFormat.JsonObject
                    : AepModelStructuredOutputFormat.JsonSchema) != true)
            throw new AepServerException("unsupported_option", "The Foundry deployment does not advertise the requested output format.", 400);
        if (effort is not null)
        {
            var reasoning = specification?.Features.Reasoning;
            if (reasoning?.Support is not (AepModelFeatureSupport.Native or AepModelFeatureSupport.Emulated or AepModelFeatureSupport.Partial)
                || effort != "default" && (!Enum.TryParse<AepModelReasoningEffort>(effort, true, out var parsed)
                    || !reasoning.Efforts.ContainsKey(parsed)))
                throw new AepServerException("unsupported_option", "The Foundry deployment does not advertise the requested reasoning effort.", 400);
        }
    }

    public Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<AepModelDescriptor>>(new AepServerException(
            "bound_value_required", "Foundry model discovery requires provider values.", 422));

    public async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(
        IReadOnlyList<AepBoundValue>? boundValues,
        CancellationToken cancellationToken = default)
    {
        using var diagnostics = new FoundryDiagnostics(diagnosticsLogger, "discovery", null);
        try
        {
            using var lease = await connectionResolver.ResolveAsync(boundValues, cancellationToken);
            var result = await ListModelsCoreAsync(lease.Connection, new FoundryRequestAuthenticator(lease.Connection), diagnostics, cancellationToken);
            diagnostics.Complete("success");
            return result;
        }
        catch (AepServerException exception) { diagnostics.Complete(exception.Code); throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { diagnostics.Complete("cancelled"); throw; }
    }

    private async Task<IReadOnlyList<AepModelDescriptor>> ListModelsCoreAsync(
        FoundryConnection connection,
        FoundryRequestAuthenticator authenticator,
        FoundryDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var models = new Dictionary<string, AepModelDescriptor>(StringComparer.Ordinal);
        var current = connection.DeploymentsEndpoint();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            if (page >= options.MaximumDiscoveryPages)
                throw new AepServerException("discovery_limit", "Foundry deployment discovery exceeded its page limit.");
            ValidatePageUri(current, connection);
            if (!visited.Add(current.AbsoluteUri))
                throw new AepServerException("discovery_invalid", "Foundry deployment pagination contains a cycle.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await SendDiscoveryPageAsync(current, authenticator, diagnostics, timeout.Token);
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
                        models.Add(name, new AepModelDescriptor(
                            name,
                            name,
                            CreateSpecification(item),
                            new AepModelIdentity(
                                SafeIdentity(item, "modelPublisher"),
                                SafeIdentity(item, "modelName"),
                                SafeIdentity(item, "modelVersion"))));
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

    private async Task<HttpResponseMessage> SendDiscoveryPageAsync(
        Uri endpoint,
        FoundryRequestAuthenticator authenticator,
        FoundryDiagnostics diagnostics,
        CancellationToken cancellationToken)
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

    public Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AepProviderHealth("available"));

    private static void ValidatePageUri(Uri uri, FoundryConnection connection)
    {
        var first = connection.DeploymentsEndpoint();
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

    private static AepModelSpecification CreateSpecification(JsonElement item)
    {
        var tools = ReadBooleanCapability(item, "toolCalling", "tool_calls", "functionCalling");
        var jsonObject = ReadBooleanCapability(item, "jsonObject", "json_object");
        var jsonSchema = ReadBooleanCapability(item, "jsonSchema", "json_schema");
        var reasoning = ReadBooleanCapability(item, "reasoning");
        var formats = new Dictionary<AepModelStructuredOutputFormat, AepModelStructuredOutputFormatSpecification>();
        if (jsonObject == true) formats[AepModelStructuredOutputFormat.JsonObject] = new();
        if (jsonSchema == true) formats[AepModelStructuredOutputFormat.JsonSchema] = new();
        return new AepModelSpecification
        {
            Input = [AepModelContentType.Text],
            Output = [AepModelContentType.Text],
            Features = new AepModelFeatureSpecifications
            {
                Streaming = new() { Support = AepModelFeatureSupport.Native },
                Tools = new()
                {
                    Support = Support(tools),
                    Modes = tools == true
                        ? new Dictionary<AepModelToolMode, AepModelToolModeSpecification>
                        {
                            [AepModelToolMode.Function] = new()
                        }
                        : new Dictionary<AepModelToolMode, AepModelToolModeSpecification>()
                },
                StructuredOutput = new()
                {
                    Support = Support(jsonObject == true || jsonSchema == true
                        ? true
                        : jsonObject == false || jsonSchema == false ? false : null),
                    Formats = formats
                },
                Reasoning = new()
                {
                    Support = Support(reasoning),
                    Efforts = reasoning == true ? ReadReasoningEfforts(item) :
                        new Dictionary<AepModelReasoningEffort, AepModelReasoningEffortSpecification>()
                }
            }
        };
    }

    private static AepModelFeatureSupport Support(bool? value) => value switch
    {
        true => AepModelFeatureSupport.Native,
        false => AepModelFeatureSupport.Unsupported,
        null => AepModelFeatureSupport.Unknown
    };

    private static bool? ReadBooleanCapability(JsonElement item, params string[] names)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object) return null;
        var foundFalse = false;
        foreach (var capability in capabilities.EnumerateObject())
        {
            if (!names.Contains(capability.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (capability.Value.ValueKind == JsonValueKind.True
                || capability.Value.ValueKind == JsonValueKind.String
                && bool.TryParse(capability.Value.GetString(), out var parsedTrue) && parsedTrue)
                return true;
            if (capability.Value.ValueKind == JsonValueKind.False
                || capability.Value.ValueKind == JsonValueKind.String
                && bool.TryParse(capability.Value.GetString(), out var parsedFalse) && !parsedFalse)
                foundFalse = true;
        }
        return foundFalse ? false : null;
    }

    private static IReadOnlyDictionary<AepModelReasoningEffort, AepModelReasoningEffortSpecification> ReadReasoningEfforts(JsonElement item)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities)
            || !capabilities.TryGetProperty("reasoningEfforts", out var efforts)
            || efforts.ValueKind != JsonValueKind.Array || efforts.GetArrayLength() > 8)
            return new Dictionary<AepModelReasoningEffort, AepModelReasoningEffortSpecification>();
        return efforts.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => Enum.TryParse<AepModelReasoningEffort>(value.GetString(), true, out var parsed)
                ? (AepModelReasoningEffort?)parsed : null)
            .Where(value => value.HasValue).Select(value => value!.Value).Distinct().Order()
            .ToDictionary(value => value, _ => new AepModelReasoningEffortSpecification());
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? SafeIdentity(JsonElement item, string property)
    {
        var value = GetString(item, property);
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value == value.Trim() && !value.Any(char.IsControl)
            ? value
            : null;
    }
}
