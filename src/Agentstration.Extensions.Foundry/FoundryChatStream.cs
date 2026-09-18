using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.Foundry;

internal static class FoundryChatStream
{
    private const int MaximumStreamBytes = 2 * 1024 * 1024;
    private const int MaximumLineBytes = 128 * 1024;
    private const int MaximumEvents = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async IAsyncEnumerable<AepChatUpdate> ExecuteAsync(
        HttpClient client, FoundryExtensionOptions options, FoundryRequestAuthenticator authenticator,
        AepChatRequest chat, FoundryDiagnostics diagnostics, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = FoundryChatCompletion.BuildRequest(chat, streaming: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, AepProtocol.JsonOptions);
        if (bytes.Length > FoundryChatCompletion.MaximumRequestBytes)
            throw new AepServerException("request_limit", "Foundry chat request exceeds its size limit.", 400);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.InferenceEndpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute))
        { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        HttpResponseMessage response;
        try
        {
            await authenticator.ApplyInferenceAsync(request, options, timeout.Token);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AepServerException("provider_timeout", "Foundry chat stream timed out."); }
        catch (HttpRequestException exception)
        { throw new AepServerException("provider_unavailable", "Foundry chat stream is unavailable.", innerException: exception); }
        using (response)
        {
            diagnostics.SetStatus(response.StatusCode);
            if (!response.IsSuccessStatusCode) throw await FoundryChatCompletion.FailureAsync(response, timeout.Token);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw FoundryChatCompletion.InvalidResponse("Foundry chat returned a non-streaming response.");
            var state = new StreamState(chat.Model, chat.Tools);
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AepServerException("provider_timeout", "Foundry chat stream timed out."); }
            catch (IOException exception)
            { throw new AepServerException("provider_unavailable", "Foundry chat stream was interrupted.", innerException: exception); }
            catch (HttpRequestException exception)
            { throw new AepServerException("provider_unavailable", "Foundry chat stream was interrupted.", innerException: exception); }
            await using (stream)
            await foreach (var data in ReadEventsAsync(stream, timeout.Token))
            {
                if (data == "[DONE]")
                {
                    foreach (var update in state.Complete()) yield return update;
                    yield break;
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException exception)
                { throw new AepServerException("invalid_response", "Foundry chat returned malformed stream JSON.", innerException: exception); }
                using (document)
                    foreach (var update in state.Accept(document.RootElement)) yield return update;
            }
            throw FoundryChatCompletion.InvalidResponse("Foundry chat stream ended without a completion marker.");
        }
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        var data = new StringBuilder();
        var total = 0;
        var events = 0;
        var sawCarriageReturn = false;
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AepServerException("provider_timeout", "Foundry chat stream timed out."); }
            catch (IOException exception)
            { throw new AepServerException("provider_unavailable", "Foundry chat stream was interrupted.", innerException: exception); }
            catch (HttpRequestException exception)
            { throw new AepServerException("provider_unavailable", "Foundry chat stream was interrupted.", innerException: exception); }
            if (read == 0) break;
            total += read;
            if (total > MaximumStreamBytes)
                throw new AepServerException("response_limit", "Foundry chat stream exceeds its size limit.");
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value == '\n')
                {
                    if (sawCarriageReturn) sawCarriageReturn = false;
                    var text = Decode(line);
                    line.SetLength(0);
                    if (text.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            if (++events > MaximumEvents)
                                throw new AepServerException("response_limit", "Foundry chat stream exceeds its event limit.");
                            yield return data.ToString();
                            data.Clear();
                        }
                    }
                    else if (text.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(text.AsSpan(5).TrimStart());
                    }
                }
                else
                {
                    if (sawCarriageReturn)
                    {
                        line.WriteByte((byte)'\r');
                        sawCarriageReturn = false;
                    }
                    if (value == '\r') sawCarriageReturn = true;
                    else line.WriteByte(value);
                    if (line.Length > MaximumLineBytes || data.Length + line.Length > MaximumLineBytes)
                        throw new AepServerException("response_limit", "Foundry chat stream line exceeds its size limit.");
                }
            }
        }
        // A completed stream must contain an explicit [DONE] event.
    }

    private static string Decode(MemoryStream line)
    {
        try { return StrictUtf8.GetString(line.GetBuffer().AsSpan(0, (int)line.Length)); }
        catch (DecoderFallbackException exception)
        { throw new AepServerException("invalid_response", "Foundry chat stream contains invalid UTF-8.", innerException: exception); }
    }

    private sealed class StreamState(string deployment, IReadOnlyList<AepToolDefinition>? declaredTools)
    {
        private readonly SortedDictionary<int, ToolCallPart> calls = [];
        private AepFinishReason? finish;
        private AepUsage? usage;
        private bool sawChoice;

        public IEnumerable<AepChatUpdate> Accept(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1)
                throw FoundryChatCompletion.InvalidResponse("Foundry chat returned invalid streaming choices.");
            if (choices.GetArrayLength() == 1)
            {
                if (finish is not null)
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned content after its finish reason.");
                sawChoice = true;
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("delta", out var delta)
                    || delta.ValueKind != JsonValueKind.Object)
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid delta.");
                if (delta.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"))
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned a non-assistant delta.");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    if (content.ValueKind != JsonValueKind.String)
                        throw FoundryChatCompletion.InvalidResponse("Foundry chat returned non-text content.");
                    var text = content.GetString()!;
                    if (text.Length > 0) yield return new AepChatUpdate([AepContent.FromText(text)], AepRole.Assistant, deployment);
                }
                if (delta.TryGetProperty("tool_calls", out var toolCalls))
                {
                    if (toolCalls.ValueKind != JsonValueKind.Array)
                        throw FoundryChatCompletion.InvalidResponse("Foundry chat returned invalid tool calls.");
                    foreach (var part in toolCalls.EnumerateArray()) AddToolPart(part);
                }
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                {
                    if (reason.ValueKind != JsonValueKind.String)
                        throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid finish reason.");
                    finish = reason.GetString() switch
                    {
                        "stop" => AepFinishReason.Stop,
                        "length" => AepFinishReason.Length,
                        "tool_calls" => AepFinishReason.ToolCalls,
                        "content_filter" => throw new AepServerException("content_filtered", "Foundry filtered the chat completion."),
                        _ => throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an unsupported finish reason.")
                    };
                }
            }
            if (root.TryGetProperty("usage", out var rawUsage) && rawUsage.ValueKind != JsonValueKind.Null)
            {
                if (finish is null || usage is not null || rawUsage.ValueKind != JsonValueKind.Object)
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned invalid token usage.");
                usage = new AepUsage(
                    FoundryChatCompletion.ReadUsage(rawUsage, "prompt_tokens"),
                    FoundryChatCompletion.ReadUsage(rawUsage, "completion_tokens"),
                    FoundryChatCompletion.ReadUsage(rawUsage, "total_tokens"));
            }
        }

        public IEnumerable<AepChatUpdate> Complete()
        {
            if (!sawChoice || finish is null || (calls.Count > 0) != (finish == AepFinishReason.ToolCalls))
                throw FoundryChatCompletion.InvalidResponse("Foundry chat stream ended without a valid finish reason.");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls.Values)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an incomplete tool call.");
                if (!seenIds.Add(call.Id) || declaredTools is null || !declaredTools.Any(tool => tool.Name == call.Name))
                    throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an undeclared or duplicate tool call.");
                JsonDocument document;
                try { document = JsonDocument.Parse(call.Arguments.ToString(), new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException exception)
                { throw new AepServerException("invalid_response", "Foundry chat returned invalid tool arguments.", innerException: exception); }
                using (document)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw FoundryChatCompletion.InvalidResponse("Foundry chat returned non-object tool arguments.");
                    yield return new AepChatUpdate(
                        [new AepContent { Kind = AepContentKind.ToolCall,
                            ToolCall = new AepToolCall(call.Id, call.Name, document.RootElement.Clone()) }],
                        AepRole.Assistant, deployment);
                }
            }
            if (usage is not null) yield return new AepChatUpdate([], Usage: usage);
            yield return new AepChatUpdate([], FinishReason: finish);
        }

        private void AddToolPart(JsonElement part)
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("index", out var rawIndex)
                || !rawIndex.TryGetInt32(out var index) || index is < 0 or >= 16)
                throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid tool index.");
            if (!calls.TryGetValue(index, out var call))
            {
                call = new ToolCallPart();
                calls.Add(index, call);
            }
            if (part.TryGetProperty("id", out var id))
            {
                if (id.ValueKind != JsonValueKind.String || call.Id is not null) throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid tool ID.");
                call.Id = id.GetString();
            }
            if (part.TryGetProperty("function", out var function))
            {
                if (function.ValueKind != JsonValueKind.Object) throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid function.");
                if (function.TryGetProperty("name", out var name))
                {
                    if (name.ValueKind != JsonValueKind.String || call.Name is not null) throw FoundryChatCompletion.InvalidResponse("Foundry chat returned an invalid tool name.");
                    call.Name = name.GetString();
                }
                if (function.TryGetProperty("arguments", out var arguments))
                {
                    if (arguments.ValueKind != JsonValueKind.String) throw FoundryChatCompletion.InvalidResponse("Foundry chat returned invalid tool arguments.");
                    call.Arguments.Append(arguments.GetString());
                }
            }
            if (call.Id is { Length: > 128 } || call.Name is { Length: > 128 } || call.Arguments.Length > 64 * 1024)
                throw new AepServerException("response_limit", "Foundry chat tool call exceeds its size limit.");
        }

        private sealed class ToolCallPart
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
