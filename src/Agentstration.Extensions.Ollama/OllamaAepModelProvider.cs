using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace Agentstration.Extensions.Ollama;

public sealed record OllamaExtensionOptions(Uri Endpoint);

public sealed class OllamaAepModelProvider(IChatClient chatClient, OllamaApiClient apiClient) : IAepModelProvider
{
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "ollama",
        "Ollama",
        new AepModelProviderCapabilities(
            Chat: true,
            Streaming: true,
            Tools: true,
            Thinking: true,
            StructuredOutput: true,
            Vision: true,
            ModelDiscovery: true));

    public async Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        try
        {
            var response = await chatClient.GetResponseAsync(MapMessages(request.Messages), MapOptions(request), cancellationToken);
            return new AepChatResponse(
                response.Messages.Select(MapMessage).ToArray(),
                response.ModelId ?? request.Model,
                MapFinishReason(response.FinishReason),
                response.Usage is null ? null : new AepUsage(response.Usage.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) { throw new AepServerException("provider_unavailable", "Ollama is unreachable.", innerException: exception); }
        catch (Exception exception) { throw new AepServerException("request_failed", "Ollama rejected the chat request.", innerException: exception); }
    }

    public async IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(
        AepChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate(request);
        IAsyncEnumerable<ChatResponseUpdate> updates;
        try { updates = chatClient.GetStreamingResponseAsync(MapMessages(request.Messages), MapOptions(request), cancellationToken); }
        catch (Exception exception) { throw new AepServerException("request_failed", "Ollama rejected the streaming request.", innerException: exception); }
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            yield return new AepChatUpdate(
                MapContents(update.Contents),
                MapRole(update.Role),
                update.ModelId ?? request.Model,
                MapFinishReason(update.FinishReason));
        }
    }

    public async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await apiClient.ListLocalModelsAsync(cancellationToken);
        return models.Select(model =>
        {
            var name = model.Name ?? model.ModelName ?? string.Empty;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(model.Details?.ParameterSize)) metadata["parameterSize"] = model.Details.ParameterSize;
            if (!string.IsNullOrWhiteSpace(model.Details?.QuantizationLevel)) metadata["quantization"] = model.Details.QuantizationLevel;
            return new AepModelDescriptor(name, name, ["chat"], metadata);
        }).Where(value => !string.IsNullOrWhiteSpace(value.Id)).ToArray();
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await apiClient.IsRunningAsync(cancellationToken)
                ? new AepProviderHealth("available")
                : new AepProviderHealth("unavailable", "Ollama did not respond to its version probe.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new AepProviderHealth("unavailable", exception.Message); }
    }

    private static void Validate(AepChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model)) throw new AepServerException("model_unavailable", "An Ollama model name is required.", 400);
        if (request.Messages.Count == 0) throw new AepServerException("invalid_request", "At least one message is required.", 400);
    }

    private static IEnumerable<ChatMessage> MapMessages(IEnumerable<AepMessage> messages) => messages.Select(message =>
    {
        var contents = new List<AIContent>();
        foreach (var content in message.Contents)
        {
            if (content.Kind == AepContentKind.Text && content.Text is not null) contents.Add(new TextContent(content.Text));
            else if (content.ToolCall is { } call)
            {
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments, AepProtocol.JsonOptions) ?? [];
                contents.Add(new FunctionCallContent(call.Id, call.Name, arguments));
            }
            else if (content.ToolResult is { } result) contents.Add(new FunctionResultContent(result.CallId, result.Result));
        }
        return new ChatMessage(MapRole(message.Role), contents) { AuthorName = message.AuthorName };
    });

    private static ChatOptions MapOptions(AepChatRequest request)
    {
        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = request.Options?.Temperature,
            MaxOutputTokens = request.Options?.MaxOutputTokens,
            TopP = request.Options?.TopP,
            TopK = request.Options?.TopK,
            Seed = request.Options?.Seed,
            StopSequences = request.Options?.StopSequences?.ToList()
        };
        if (request.Tools is { Count: > 0 })
            options.Tools = request.Tools.Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.Parameters)).ToList();
        ApplyNativeOptions(options, request.Options?.AdditionalOptions);
        return options;
    }

    private static void ApplyNativeOptions(ChatOptions options, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null || !values.TryGetValue("ollama", out var root) || root.ValueKind != JsonValueKind.Object) return;
        options.AdditionalProperties ??= [];
        foreach (var item in root.EnumerateObject())
        {
            switch (item.Name)
            {
                case "think":
                    options.AdditionalProperties["think"] = item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.Value.GetBoolean() : item.Value.GetString();
                    break;
                case "keepAlive": options.AdditionalProperties["keep_alive"] = item.Value.GetString(); break;
                case "contextSize": options.AddOllamaOption(OllamaOption.NumCtx, item.Value.GetInt32()); break;
                case "numGpu": options.AddOllamaOption(OllamaOption.NumGpu, item.Value.GetInt32()); break;
                case "numThread": options.AddOllamaOption(OllamaOption.NumThread, item.Value.GetInt32()); break;
                case "numBatch": options.AddOllamaOption(OllamaOption.NumBatch, item.Value.GetInt32()); break;
                case "mirostat": options.AddOllamaOption(OllamaOption.MiroStat, item.Value.GetInt32()); break;
                case "endpointMode" when string.Equals(item.Value.GetString(), "generate", StringComparison.OrdinalIgnoreCase):
                    throw new AepServerException("invalid_request", "Ollama endpointMode 'generate' is incompatible with chat.", 400);
                case "additionalOptions" when item.Value.ValueKind == JsonValueKind.Object:
                    foreach (var additional in item.Value.EnumerateObject()) options.AddOllamaOption(new OllamaOption(additional.Name), additional.Value);
                    break;
            }
        }
    }

    private static AepMessage MapMessage(ChatMessage message) => new(MapRole(message.Role), MapContents(message.Contents), message.AuthorName);

    private static IReadOnlyList<AepContent> MapContents(IEnumerable<AIContent> contents)
    {
        var mapped = new List<AepContent>();
        foreach (var content in contents)
        {
            if (content is TextContent text) mapped.Add(AepContent.FromText(text.Text));
            else if (content is FunctionCallContent call)
                mapped.Add(new AepContent { Kind = AepContentKind.ToolCall, ToolCall = new(call.CallId, call.Name, JsonSerializer.SerializeToElement(call.Arguments, AepProtocol.JsonOptions)) });
            else if (content is FunctionResultContent result)
                mapped.Add(new AepContent { Kind = AepContentKind.ToolResult, ToolResult = new(result.CallId, JsonSerializer.SerializeToElement(result.Result, AepProtocol.JsonOptions)) });
        }
        return mapped;
    }

    private static ChatRole MapRole(AepRole role) => role == AepRole.System ? ChatRole.System : role == AepRole.Assistant ? ChatRole.Assistant : role == AepRole.Tool ? ChatRole.Tool : ChatRole.User;
    private static AepRole MapRole(ChatRole role) => role == ChatRole.System ? AepRole.System : role == ChatRole.Assistant ? AepRole.Assistant : role == ChatRole.Tool ? AepRole.Tool : AepRole.User;
    private static AepRole? MapRole(ChatRole? role) => role is null ? null : MapRole(role.Value);
    private static AepFinishReason? MapFinishReason(ChatFinishReason? reason) => reason == ChatFinishReason.Stop ? AepFinishReason.Stop : reason == ChatFinishReason.Length ? AepFinishReason.Length : reason == ChatFinishReason.ToolCalls ? AepFinishReason.ToolCalls : reason == ChatFinishReason.ContentFilter ? AepFinishReason.ContentFilter : reason is null ? null : AepFinishReason.Other;
}
