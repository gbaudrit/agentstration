using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.Foundry;

internal static class FoundryChatCompletion
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumErrorBytes = 8192;

    public static async Task<AepChatResponse> ExecuteAsync(
        HttpClient client,
        FoundryExtensionOptions options,
        FoundryConnection connection,
        FoundryRequestAuthenticator authenticator,
        AepChatRequest chat,
        FoundryDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var body = BuildRequest(chat);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, AepProtocol.JsonOptions);
        if (bytes.Length > MaximumRequestBytes)
            throw new AepServerException("request_limit", "Foundry chat request exceeds its size limit.", 400);

        var endpoint = new Uri(connection.InferenceEndpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            await authenticator.ApplyInferenceAsync(request, timeout.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            diagnostics.SetStatus(response.StatusCode);
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, timeout.Token);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new AepServerException("invalid_response", "Foundry chat returned a non-JSON response.");
            var responseBytes = await ReadBoundedAsync(response.Content, MaximumResponseBytes, timeout.Token);
            JsonDocument document;
            try { document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException exception)
            {
                throw new AepServerException("invalid_response", "Foundry chat returned invalid JSON.", innerException: exception);
            }
            using (document) return ParseResponse(document.RootElement, chat.Model, chat.Tools);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AepServerException("provider_timeout", "Foundry chat timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AepServerException("provider_unavailable", "Foundry chat is unavailable.", innerException: exception);
        }
        catch (IOException exception)
        {
            throw new AepServerException("provider_unavailable", "Foundry chat response was interrupted.", innerException: exception);
        }
    }

    internal static JsonObject BuildRequest(AepChatRequest chat, bool streaming = false)
    {
        if (chat is null || string.IsNullOrWhiteSpace(chat.Model) || chat.Model.Length > 256
            || chat.Messages is null || chat.Messages.Count is < 1 or > 64)
            throw Invalid("A bounded deployment name and 1 to 64 messages are required.");
        if (chat.Tools is { Count: > 0 } declaredTools)
        {
            if (declaredTools.Count > 16) throw Invalid("Foundry chat accepts at most 16 tools.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in declaredTools)
                if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 128 || !names.Add(tool.Name)
                    || tool.Description is { Length: > 1024 } || tool.Parameters.ValueKind != JsonValueKind.Object
                    || tool.Parameters.GetRawText().Length > 65536)
                    throw Invalid("Foundry chat tool definition is invalid.");
        }
        if (chat.Metadata is { Count: > 0 }) throw Unsupported("Chat metadata is not supported by Foundry chat yet.");

        var messages = new JsonArray();
        foreach (var message in chat.Messages)
        {
            if (message is null || message.Contents is null || message.Contents.Count == 0)
                throw Invalid("Each Foundry chat message requires content.");
            if (message.Role == AepRole.Tool)
            {
                if (message.Contents.Count != 1 || message.Contents[0].Kind != AepContentKind.ToolResult
                    || message.Contents[0].ToolResult is not { } result || string.IsNullOrWhiteSpace(result.CallId)
                    || result.Result.ValueKind == JsonValueKind.Undefined || result.IsError
                    || message.AuthorName is not null)
                    throw Unsupported("Only a single successful tool result is supported as a tool message.");
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["content"] = result.Result.ValueKind == JsonValueKind.String
                        ? result.Result.GetString() : result.Result.GetRawText()
                });
                continue;
            }
            var role = message.Role switch
            {
                AepRole.System => "system",
                AepRole.User => "user",
                AepRole.Assistant => "assistant",
                _ => throw Unsupported("The AEP message role is not supported by Foundry chat.")
            };
            if (message.Contents.Any(content => content is null || (content.Kind != AepContentKind.Text && content.Kind != AepContentKind.ToolCall)))
                throw Unsupported("Foundry chat supports text and assistant tool calls only.");
            if (message.Role != AepRole.Assistant && message.Contents.Any(content => content.Kind == AepContentKind.ToolCall))
                throw Unsupported("Only assistant messages can contain tool calls.");
            if (message.Contents.Any(content => content.Kind == AepContentKind.Text && content.Text is null))
                throw Invalid("Foundry chat text content is invalid.");
            var mapped = new JsonObject
            {
                ["role"] = role,
                ["content"] = string.Concat(message.Contents.Where(content => content.Kind == AepContentKind.Text).Select(content => content.Text))
            };
            if (message.Contents.Any(content => content.Kind == AepContentKind.ToolCall))
            {
                var calls = new JsonArray();
                foreach (var content in message.Contents.Where(content => content.Kind == AepContentKind.ToolCall))
                {
                    var call = content.ToolCall;
                    if (call is null || string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 128
                        || string.IsNullOrWhiteSpace(call.Name) || call.Name.Length > 128
                        || call.Arguments.ValueKind != JsonValueKind.Object)
                        throw Invalid("Foundry chat tool call is invalid.");
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.GetRawText() }
                    });
                }
                mapped["tool_calls"] = calls;
            }
            if (message.AuthorName is not null)
            {
                if (string.IsNullOrWhiteSpace(message.AuthorName) || message.AuthorName.Length > 64)
                    throw Invalid("Message author name is invalid.");
                mapped["name"] = message.AuthorName;
            }
            messages.Add(mapped);
        }

        var body = new JsonObject { ["model"] = chat.Model, ["messages"] = messages, ["stream"] = streaming };
        if (chat.Tools is { Count: > 0 } definitions)
        {
            var tools = new JsonArray();
            foreach (var tool in definitions)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
                    }
                });
            body["tools"] = tools;
        }
        if (streaming) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        ApplyOptions(body, chat.Options);
        return body;
    }

    private static void ApplyOptions(JsonObject body, AepModelOptions? options)
    {
        if (options is null) return;
        if (options.TopK is not null || options.NativeOptions is not null)
            throw Unsupported("TopK and native options are not supported by Foundry chat.");
        var format = RequestedFormat(options);
        if (format is "json_object" or "json_schema")
            body["response_format"] = JsonNode.Parse(options.ResponseFormat!.Value.GetRawText());
        var effort = RequestedReasoningEffort(options);
        if (effort is not null and not "default") body["reasoning_effort"] = effort;
        if (options.Temperature is { } temperature)
        {
            if (temperature is < 0 or > 2 || !float.IsFinite(temperature)) throw Invalid("Temperature must be between 0 and 2.");
            body["temperature"] = temperature;
        }
        if (options.TopP is { } topP)
        {
            if (topP is < 0 or > 1 || !float.IsFinite(topP)) throw Invalid("TopP must be between 0 and 1.");
            body["top_p"] = topP;
        }
        if (options.MaxOutputTokens is { } maximum)
        {
            if (maximum is < 1 or > 131072) throw Invalid("MaxOutputTokens is outside the supported range.");
            body["max_completion_tokens"] = maximum;
        }
        if (options.Seed is { } seed) body["seed"] = seed;
        if (options.StopSequences is { } stop)
        {
            if (stop.Count is < 1 or > 4 || stop.Any(value => string.IsNullOrEmpty(value) || value.Length > 256))
                throw Invalid("StopSequences requires 1 to 4 bounded non-empty strings.");
            body["stop"] = new JsonArray(stop.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
    }

    internal static string? RequestedFormat(AepModelOptions? options)
    {
        if (options?.ResponseFormat is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String) throw Unsupported("The response format is invalid.");
        var format = type.GetString();
        if (format is "text" or "json_object")
        {
            if (value.EnumerateObject().Count() != 1) throw Unsupported("The response format has unsupported fields.");
            return format;
        }
        if (format != "json_schema" || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("json_schema", out var definition) || definition.ValueKind != JsonValueKind.Object
            || definition.EnumerateObject().Count() is < 2 or > 3
            || !definition.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || name.GetString() is not { Length: > 0 and <= 64 }
            || !definition.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object
            || schema.GetRawText().Length > 65536)
            throw Unsupported("The JSON schema response format is invalid or exceeds its limit.");
        if (definition.TryGetProperty("strict", out var strict) && strict.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw Unsupported("The JSON schema strict flag is invalid.");
        if (definition.EnumerateObject().Any(property => property.Name is not ("name" or "schema" or "strict")))
            throw Unsupported("The JSON schema response format has unsupported fields.");
        try { using var _ = JsonDocument.Parse(schema.GetRawText(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Unsupported("The JSON schema exceeds the supported depth."); }
        return format;
    }

    internal static string? RequestedReasoningEffort(AepModelOptions? options)
    {
        if (options?.AdditionalOptions is not { Count: > 0 } additional) return null;
        if (additional.Count > 2 || additional.Keys.Any(key => key is not ("reasoning_enabled" or "reasoning_effort")))
            throw Unsupported("The additional options are not supported by Foundry chat.");
        bool? enabled = null;
        if (additional.TryGetValue("reasoning_enabled", out var flag))
        {
            if (flag.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                throw Unsupported("The reasoning flag is invalid.");
            enabled = flag.GetBoolean();
        }
        string? effort = null;
        if (additional.TryGetValue("reasoning_effort", out var value))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("minimal" or "low" or "medium" or "high"))
                throw Unsupported("The reasoning effort is invalid.");
            effort = value.GetString();
        }
        if (enabled == false && effort is not null) throw Unsupported("Disabled reasoning cannot specify an effort.");
        return enabled == false ? "none" : effort ?? (enabled == true ? "default" : null);
    }

    private static AepChatResponse ParseResponse(JsonElement root, string deployment, IReadOnlyList<AepToolDefinition>? declaredTools)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw InvalidResponse("Foundry chat returned no single completion choice.");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object || GetString(message, "role") != "assistant")
            throw InvalidResponse("Foundry chat returned no assistant message.");
        var finish = GetString(choice, "finish_reason") switch
        {
            "stop" => AepFinishReason.Stop,
            "length" => AepFinishReason.Length,
            "content_filter" => throw new AepServerException("content_filtered", "Foundry filtered the chat completion."),
            "tool_calls" => AepFinishReason.ToolCalls,
            _ => throw InvalidResponse("Foundry chat returned an unsupported finish reason.")
        };
        var contents = new List<AepContent>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            contents.Add(AepContent.FromText(content.GetString()!));
        else if (finish != AepFinishReason.ToolCalls)
            throw InvalidResponse("Foundry chat returned non-text assistant content.");
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
        {
            if (finish != AepFinishReason.ToolCalls || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() is < 1 or > 16)
                throw InvalidResponse("Foundry chat returned invalid tool calls.");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || GetString(call, "type") != "function"
                    || GetString(call, "id") is not { Length: > 0 and <= 128 } id
                    || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object
                    || GetString(function, "name") is not { Length: > 0 and <= 128 } name
                    || GetString(function, "arguments") is not { Length: <= 65536 } arguments)
                    throw InvalidResponse("Foundry chat returned an invalid tool call.");
                if (!seenIds.Add(id) || declaredTools is null || !declaredTools.Any(tool => tool.Name == name))
                    throw InvalidResponse("Foundry chat returned an undeclared or duplicate tool call.");
                JsonDocument argumentsDocument;
                try { argumentsDocument = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException exception)
                { throw new AepServerException("invalid_response", "Foundry chat returned invalid tool arguments.", innerException: exception); }
                using (argumentsDocument)
                {
                    if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                        throw InvalidResponse("Foundry chat returned non-object tool arguments.");
                    contents.Add(new AepContent
                    {
                        Kind = AepContentKind.ToolCall,
                        ToolCall = new AepToolCall(id, name, argumentsDocument.RootElement.Clone())
                    });
                }
            }
        }
        else if (finish == AepFinishReason.ToolCalls)
            throw InvalidResponse("Foundry chat returned no tool calls.");
        AepUsage? usage = null;
        if (root.TryGetProperty("usage", out var rawUsage) && rawUsage.ValueKind != JsonValueKind.Null)
        {
            if (rawUsage.ValueKind != JsonValueKind.Object) throw InvalidResponse("Foundry chat returned invalid token usage.");
            usage = new AepUsage(
                ReadUsage(rawUsage, "prompt_tokens"),
                ReadUsage(rawUsage, "completion_tokens"),
                ReadUsage(rawUsage, "total_tokens"));
        }
        return new AepChatResponse(
            [new AepMessage(AepRole.Assistant, contents)],
            deployment,
            finish,
            usage);
    }

    internal static long? ReadUsage(JsonElement usage, string property)
    {
        if (!usage.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
            throw InvalidResponse("Foundry chat returned invalid token usage.");
        return count;
    }

    internal static async Task<AepServerException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            return new AepServerException("provider_redirect_denied", "Foundry redirected a credential-bearing chat request.");
        if (response.StatusCode == HttpStatusCode.BadRequest
            && string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await ReadBoundedAsync(response.Content, MaximumErrorBytes, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && GetString(error, "code") == "content_filter")
                    return new AepServerException("content_filtered", "Foundry filtered the chat request.");
            }
            catch (JsonException) { }
        }
        var code = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "invalid_request",
            HttpStatusCode.Unauthorized => "authentication_failed",
            HttpStatusCode.Forbidden => "authorization_failed",
            HttpStatusCode.NotFound => "model_unavailable",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "provider_timeout",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "provider_unavailable"
        };
        return new AepServerException(code, $"Foundry chat returned HTTP {(int)response.StatusCode}.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum)
            throw new AepServerException("response_limit", "Foundry chat response exceeds its size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximum)
                throw new AepServerException("response_limit", "Foundry chat response exceeds its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static AepServerException Invalid(string message) => new("invalid_request", message, 400);
    private static AepServerException Unsupported(string message) => new("unsupported_option", message, 400);
    internal static AepServerException InvalidResponse(string message) => new("invalid_response", message);
}
