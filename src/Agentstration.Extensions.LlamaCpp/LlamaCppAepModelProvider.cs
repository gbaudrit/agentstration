using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.LlamaCpp;

public sealed record LlamaCppExtensionOptions(Uri Endpoint);

public sealed class LlamaCppAepModelProvider(HttpClient httpClient) : IAepModelProvider
{
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "llamacpp",
        "llama.cpp",
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
        Validate(request);
        using var response = await SendAsync(
            HttpMethod.Post,
            "v1/chat/completions",
            BuildRequest(request, streaming: false),
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return ParseResponse(document.RootElement, request.Model);
    }

    public async IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(
        AepChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate(request);
        using var response = await SendAsync(
            HttpMethod.Post,
            "v1/chat/completions",
            BuildRequest(request, streaming: true),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var toolCalls = new Dictionary<int, ToolCallAccumulator>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart();
            if (data.Length == 0) continue;
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal)) yield break;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!TryFirstChoice(root, out var choice)) continue;
            var model = GetString(root, "model") ?? request.Model;
            var finishReason = MapFinishReason(GetString(choice, "finish_reason"));
            var contents = new List<AepContent>();
            AepRole? role = null;
            if (choice.TryGetProperty("delta", out var delta))
            {
                role = MapRole(GetString(delta, "role"));
                if (GetString(delta, "content") is { Length: > 0 } text) contents.Add(AepContent.FromText(text));
                AccumulateToolCalls(delta, toolCalls);
            }
            if (finishReason is not null && toolCalls.Count > 0)
            {
                contents.AddRange(toolCalls.OrderBy(value => value.Key).Select(value => value.Value.Build()));
                toolCalls.Clear();
            }
            if (contents.Count > 0 || finishReason is not null)
                yield return new AepChatUpdate(contents, role, model, finishReason, ParseUsage(root));
        }
    }

    public async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/models", null, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var capabilities = await GetModelCapabilitiesAsync(cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        var results = new List<AepModelDescriptor>();
        foreach (var model in models.EnumerateArray())
        {
            var id = GetString(model, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (model.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                AddMetadata(meta, metadata, "n_ctx_train", "contextLength");
                AddMetadata(meta, metadata, "n_params", "parameterCount");
                AddMetadata(meta, metadata, "size", "sizeBytes");
            }
            results.Add(new AepModelDescriptor(id, id, capabilities, metadata));
        }
        return results;
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            if (response.IsSuccessStatusCode) return new AepProviderHealth("available");
            var details = await ReadErrorMessageAsync(response, cancellationToken);
            return new AepProviderHealth(
                response.StatusCode == HttpStatusCode.ServiceUnavailable ? "loading" : "unavailable",
                details);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) { return new AepProviderHealth("unreachable", exception.Message); }
    }

    private async Task<IReadOnlyList<string>> GetModelCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<string> { "chat", "streaming", "structuredOutput" };
        try
        {
            using var response = await httpClient.GetAsync("props", cancellationToken);
            if (!response.IsSuccessStatusCode) return capabilities;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (ContainsEnabledCapability(root, "tool")) capabilities.Add("tools");
            if (ContainsEnabledCapability(root, "reason")) capabilities.Add("reasoning");
            if (root.TryGetProperty("modalities", out var modalities)
                && modalities.ValueKind == JsonValueKind.Array
                && modalities.EnumerateArray().Any(value => string.Equals(value.GetString(), "image", StringComparison.OrdinalIgnoreCase)))
            {
                capabilities.Add("vision");
            }
        }
        catch (HttpRequestException)
        {
            // Model discovery remains useful when optional capability inspection is unavailable.
        }
        return capabilities;
    }

    private static JsonObject BuildRequest(AepChatRequest request, bool streaming)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request.Messages),
            ["stream"] = streaming
        };
        ApplyOptions(body, request.Options);
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = Clone(tool.Parameters)
                }
            }).ToArray());
        }
        return body;
    }

    private static JsonArray BuildMessages(IEnumerable<AepMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var text = string.Concat(message.Contents.Where(value => value.Kind == AepContentKind.Text).Select(value => value.Text));
            var mapped = new JsonObject { ["role"] = MapRole(message.Role), ["content"] = text };
            if (!string.IsNullOrWhiteSpace(message.AuthorName)) mapped["name"] = message.AuthorName;
            var calls = message.Contents.Where(value => value.ToolCall is not null).Select(value => value.ToolCall!).ToArray();
            if (calls.Length > 0)
            {
                mapped["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.GetRawText() }
                }).ToArray());
            }
            if (message.Contents.FirstOrDefault(value => value.ToolResult is not null)?.ToolResult is { } toolResult)
            {
                mapped["tool_call_id"] = toolResult.CallId;
                mapped["content"] = toolResult.Result.ValueKind == JsonValueKind.String
                    ? toolResult.Result.GetString()
                    : toolResult.Result.GetRawText();
            }
            result.Add(mapped);
        }
        return result;
    }

    private static void ApplyOptions(JsonObject body, AepModelOptions? options)
    {
        if (options is null) return;
        Set(body, "temperature", options.Temperature);
        Set(body, "max_tokens", options.MaxOutputTokens);
        Set(body, "top_p", options.TopP);
        Set(body, "top_k", options.TopK);
        Set(body, "seed", options.Seed);
        if (options.StopSequences is { Count: > 0 })
            body["stop"] = new JsonArray(options.StopSequences.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (options.ResponseFormat is { } responseFormat) body["response_format"] = Clone(responseFormat);
        if (options.AdditionalOptions is not { } additional) return;

        JsonObject? chatTemplateKwargs = null;
        if (additional.TryGetValue("reasoning_enabled", out var reasoningEnabled)
            && reasoningEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            chatTemplateKwargs = new JsonObject { ["enable_thinking"] = reasoningEnabled.GetBoolean() };
        }
        if (additional.TryGetValue("reasoning_effort", out var reasoningEffort) && reasoningEffort.ValueKind == JsonValueKind.String)
            body["reasoning_effort"] = reasoningEffort.GetString();

        if (additional.TryGetValue("llamacpp", out var native) && native.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in native.EnumerateObject())
            {
                var target = property.Name switch
                {
                    "minP" => "min_p",
                    "typicalP" => "typical_p",
                    "repeatPenalty" => "repeat_penalty",
                    "repeatLastN" => "repeat_last_n",
                    "mirostatTau" => "mirostat_tau",
                    "mirostatEta" => "mirostat_eta",
                    "reasoningFormat" => "reasoning_format",
                    "reasoningEffort" => "reasoning_effort",
                    _ => property.Name
                };
                if (property.Name == "chatTemplateKwargs" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    chatTemplateKwargs ??= new JsonObject();
                    foreach (var value in property.Value.EnumerateObject()) chatTemplateKwargs[value.Name] = Clone(value.Value);
                }
                else if (property.Name == "additionalOptions" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var value in property.Value.EnumerateObject()) body[value.Name] = Clone(value.Value);
                }
                else body[target] = Clone(property.Value);
            }
        }
        if (chatTemplateKwargs is not null) body["chat_template_kwargs"] = chatTemplateKwargs;
    }

    private static AepChatResponse ParseResponse(JsonElement root, string fallbackModel)
    {
        if (!TryFirstChoice(root, out var choice) || !choice.TryGetProperty("message", out var message))
            throw new AepServerException("invalid_response", "llama.cpp returned no chat completion choice.");
        var contents = ParseMessageContents(message);
        var role = MapRole(GetString(message, "role")) ?? AepRole.Assistant;
        return new AepChatResponse(
            [new AepMessage(role, contents)],
            GetString(root, "model") ?? fallbackModel,
            MapFinishReason(GetString(choice, "finish_reason")),
            ParseUsage(root));
    }

    private static IReadOnlyList<AepContent> ParseMessageContents(JsonElement message)
    {
        var contents = new List<AepContent>();
        if (GetString(message, "content") is { Length: > 0 } text) contents.Add(AepContent.FromText(text));
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function)) continue;
                var id = GetString(call, "id") ?? Guid.NewGuid().ToString("N");
                var name = GetString(function, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                contents.Add(new AepContent
                {
                    Kind = AepContentKind.ToolCall,
                    ToolCall = new AepToolCall(id, name, ParseArguments(GetString(function, "arguments")))
                });
            }
        }
        return contents;
    }

    private static void AccumulateToolCalls(JsonElement delta, IDictionary<int, ToolCallAccumulator> target)
    {
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return;
        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexValue) ? indexValue.GetInt32() : target.Count;
            if (!target.TryGetValue(index, out var accumulator)) target[index] = accumulator = new ToolCallAccumulator();
            accumulator.Id ??= GetString(call, "id");
            if (!call.TryGetProperty("function", out var function)) continue;
            accumulator.Name ??= GetString(function, "name");
            if (GetString(function, "arguments") is { } arguments) accumulator.Arguments.Append(arguments);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: AepProtocol.JsonOptions);
        try { return await httpClient.SendAsync(request, completion, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) { throw new AepServerException("provider_unavailable", "llama.cpp is unreachable.", innerException: exception); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await ReadErrorMessageAsync(response, cancellationToken);
        var code = response.StatusCode == HttpStatusCode.NotFound ? "model_unavailable"
            : (int)response.StatusCode is >= 400 and < 500 ? "invalid_request"
            : "provider_unavailable";
        throw new AepServerException(code, message ?? $"llama.cpp returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("error", out var error)
                ? GetString(error, "message") ?? error.ToString()
                : document.RootElement.ToString();
        }
        catch (JsonException) { return response.ReasonPhrase; }
    }

    private static bool ContainsEnabledCapability(JsonElement element, string fragment)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.True) return true;
                if (ContainsEnabledCapability(property.Value, fragment)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) if (ContainsEnabledCapability(item, fragment)) return true;
        }
        return false;
    }

    private static void Validate(AepChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model)) throw new AepServerException("model_unavailable", "A llama.cpp model name is required.", 400);
        if (request.Messages.Count == 0) throw new AepServerException("invalid_request", "At least one message is required.", 400);
    }

    private static bool TryFirstChoice(JsonElement root, out JsonElement choice)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            choice = choices[0];
            return true;
        }
        choice = default;
        return false;
    }

    private static AepUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var input = GetInt64(usage, "prompt_tokens");
        var output = GetInt64(usage, "completion_tokens");
        var total = GetInt64(usage, "total_tokens");
        return new AepUsage(input, output, total);
    }

    private static JsonElement ParseArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return JsonSerializer.SerializeToElement(new { });
        try { return JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { value }); }
    }

    private static JsonNode? Clone(JsonElement value) => JsonNode.Parse(value.GetRawText());
    private static string? GetString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;
    private static long? GetInt64(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result : null;
    private static void Set<T>(JsonObject target, string name, T? value) where T : struct { if (value is not null) target[name] = JsonValue.Create(value.Value); }
    private static string MapRole(AepRole role) => role == AepRole.System ? "system" : role == AepRole.Assistant ? "assistant" : role == AepRole.Tool ? "tool" : "user";
    private static AepRole? MapRole(string? role) => role == "system" ? AepRole.System : role == "assistant" ? AepRole.Assistant : role == "tool" ? AepRole.Tool : role == "user" ? AepRole.User : null;
    private static AepFinishReason? MapFinishReason(string? reason) => reason == "stop" ? AepFinishReason.Stop : reason == "length" ? AepFinishReason.Length : reason == "tool_calls" ? AepFinishReason.ToolCalls : reason == "content_filter" ? AepFinishReason.ContentFilter : reason is null ? null : AepFinishReason.Other;
    private static void AddMetadata(JsonElement source, IDictionary<string, string> target, string sourceName, string targetName) { if (source.TryGetProperty(sourceName, out var value)) target[targetName] = value.ToString(); }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public AepContent Build() => new()
        {
            Kind = AepContentKind.ToolCall,
            ToolCall = new AepToolCall(Id ?? Guid.NewGuid().ToString("N"), Name ?? "unknown", ParseArguments(Arguments.ToString()))
        };
    }
}
