using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.LocalAI;

public sealed class LocalAiAepModelProvider(HttpClient httpClient) : IAepModelProvider
{
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "localai",
        "LocalAI",
        new AepModelProviderCapabilities(
            Chat: true,
            Streaming: true,
            Tools: true,
            Thinking: true,
            StructuredOutput: false,
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
        using var response = await SendAsync(
            HttpMethod.Get,
            "v1/models/capabilities",
            null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AepServerException(
                "provider_incompatible",
                "LocalAI must support GET /v1/models/capabilities for capability-safe model discovery.");
        }
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array) return [];

        var results = new List<AepModelDescriptor>();
        foreach (var model in models.EnumerateArray())
        {
            var id = GetString(model, "id");
            var nativeCapabilities = Strings(model, "capabilities");
            if (string.IsNullOrWhiteSpace(id) || !nativeCapabilities.Contains("chat", StringComparer.OrdinalIgnoreCase)) continue;

            var capabilities = new List<string> { "chat", "streaming" };
            if (nativeCapabilities.Contains("tools", StringComparer.OrdinalIgnoreCase)) capabilities.Add("tools");
            if (nativeCapabilities.Contains("thinking", StringComparer.OrdinalIgnoreCase)) capabilities.Add("reasoning");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            AddMetadataList(model, metadata, "input_modalities", "inputModalities");
            AddMetadataList(model, metadata, "output_modalities", "outputModalities");
            results.Add(new AepModelDescriptor(id, id, capabilities, metadata));
        }
        return results;
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("readyz", cancellationToken);
            if (response.IsSuccessStatusCode) return new AepProviderHealth("available");
            var details = await ReadErrorMessageAsync(response, cancellationToken);
            return new AepProviderHealth(
                response.StatusCode == HttpStatusCode.ServiceUnavailable ? "loading" : "unavailable",
                details);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) { return new AepProviderHealth("unreachable", exception.Message); }
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
        var additional = options.AdditionalOptions;
        if (additional is not null
            && additional.TryGetValue("reasoning_effort", out var reasoningEffort)
            && reasoningEffort.ValueKind == JsonValueKind.String)
            body["reasoning_effort"] = reasoningEffort.GetString();
        if (options.NativeOptions?.Values is not { ValueKind: JsonValueKind.Object } native) return;
        foreach (var property in native.EnumerateObject())
        {
            var target = property.Name switch
            {
                "frequencyPenalty" => "frequency_penalty",
                "presencePenalty" => "presence_penalty",
                _ => throw new AepServerException(
                    "invalid_request",
                    $"Unsupported LocalAI option '{property.Name}'. Only frequencyPenalty and presencePenalty are allowed.",
                    400)
            };
            body[target] = Clone(property.Value);
        }
    }

    private static AepChatResponse ParseResponse(JsonElement root, string fallbackModel)
    {
        if (!TryFirstChoice(root, out var choice) || !choice.TryGetProperty("message", out var message))
            throw new AepServerException("invalid_response", "LocalAI returned no chat completion choice.");
        return new AepChatResponse(
            [new AepMessage(MapRole(GetString(message, "role")) ?? AepRole.Assistant, ParseMessageContents(message))],
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
                var name = GetString(function, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                contents.Add(new AepContent
                {
                    Kind = AepContentKind.ToolCall,
                    ToolCall = new AepToolCall(
                        GetString(call, "id") ?? Guid.NewGuid().ToString("N"),
                        name,
                        ParseArguments(GetString(function, "arguments")))
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
        catch (HttpRequestException exception)
        {
            throw new AepServerException("provider_unavailable", "LocalAI is unreachable.", innerException: exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await ReadErrorMessageAsync(response, cancellationToken);
        var code = response.StatusCode == HttpStatusCode.NotFound ? "model_unavailable"
            : (int)response.StatusCode is >= 400 and < 500 ? "invalid_request"
            : "provider_unavailable";
        throw new AepServerException(code, message ?? $"LocalAI returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.String ? error.GetString() : GetString(error, "message") ?? error.ToString()
                : document.RootElement.ToString();
        }
        catch (JsonException) { return response.ReasonPhrase; }
    }

    private static void Validate(AepChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model)) throw new AepServerException("model_unavailable", "A LocalAI model name is required.", 400);
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
        return new AepUsage(
            GetInt64(usage, "prompt_tokens"),
            GetInt64(usage, "completion_tokens"),
            GetInt64(usage, "total_tokens"));
    }

    private static IReadOnlyList<string> Strings(JsonElement value, string name) =>
        value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];

    private static void AddMetadataList(JsonElement source, IDictionary<string, string> target, string sourceName, string targetName)
    {
        var values = Strings(source, sourceName);
        if (values.Count > 0) target[targetName] = string.Join(',', values);
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

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public AepContent Build() => new()
        {
            Kind = AepContentKind.ToolCall,
            ToolCall = new AepToolCall(
                Id ?? Guid.NewGuid().ToString("N"),
                Name ?? "unknown",
                ParseArguments(Arguments.ToString()))
        };
    }
}
