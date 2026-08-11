using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.Extensions.AI;

namespace Agentstration.Aep.MicrosoftExtensionsAI;

public sealed class AepChatClient(
    AepModelProviderClient provider,
    string model,
    IReadOnlyDictionary<string, JsonElement>? providerOptions = null) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await provider.ChatAsync(MapRequest(messages, options), cancellationToken);
        var result = new ChatResponse(response.Messages.Select(MapMessage).ToList())
        {
            ModelId = response.Model ?? model,
            FinishReason = MapFinishReason(response.FinishReason)
        };
        if (response.Usage is { } usage)
        {
            result.Usage = new UsageDetails
            {
                InputTokenCount = usage.InputTokens,
                OutputTokenCount = usage.OutputTokens,
                TotalTokenCount = usage.TotalTokens
            };
        }
        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in provider.ChatStreamingAsync(MapRequest(messages, options), cancellationToken).WithCancellation(cancellationToken))
        {
            var mapped = new ChatResponseUpdate(MapRole(update.Role), MapContents(update.Contents))
            {
                ModelId = update.Model ?? model,
                FinishReason = MapFinishReason(update.FinishReason)
            };
            yield return mapped;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private AepChatRequest MapRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var mappedMessages = messages.Select(message => new AepMessage(
            MapRole(message.Role),
            MapContents(message),
            message.AuthorName)).ToArray();
        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (providerOptions is not null)
            foreach (var value in providerOptions) additional[value.Key] = value.Value;
        if (options?.AdditionalProperties is not null)
            foreach (var value in options.AdditionalProperties)
                additional[value.Key] = JsonSerializer.SerializeToElement(value.Value, AepProtocol.JsonOptions);
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>()
            .Select(tool => new AepToolDefinition(tool.Name, tool.Description, tool.JsonSchema))
            .ToArray();
        return new AepChatRequest(
            options?.ModelId ?? model,
            mappedMessages,
            new AepModelOptions
            {
                Temperature = options?.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens,
                TopP = options?.TopP,
                TopK = options?.TopK,
                Seed = options?.Seed,
                StopSequences = options?.StopSequences?.ToArray(),
                AdditionalOptions = additional
            },
            tools);
    }

    private static IReadOnlyList<AepContent> MapContents(ChatMessage message)
    {
        var contents = new List<AepContent>();
        foreach (var content in message.Contents)
        {
            if (content is TextContent text) contents.Add(AepContent.FromText(text.Text));
            else if (content is FunctionCallContent call)
            {
                contents.Add(new AepContent
                {
                    Kind = AepContentKind.ToolCall,
                    ToolCall = new AepToolCall(call.CallId, call.Name, JsonSerializer.SerializeToElement(call.Arguments, AepProtocol.JsonOptions))
                });
            }
            else if (content is FunctionResultContent result)
            {
                contents.Add(new AepContent
                {
                    Kind = AepContentKind.ToolResult,
                    ToolResult = new AepToolResult(result.CallId, JsonSerializer.SerializeToElement(result.Result, AepProtocol.JsonOptions))
                });
            }
        }
        return contents;
    }

    private static ChatMessage MapMessage(AepMessage message)
    {
        return new ChatMessage(MapRole(message.Role), MapContents(message.Contents)) { AuthorName = message.AuthorName };
    }

    private static IList<AIContent> MapContents(IEnumerable<AepContent> source)
    {
        var contents = new List<AIContent>();
        foreach (var content in source)
        {
            if (content.Kind == AepContentKind.Text && content.Text is not null) contents.Add(new TextContent(content.Text));
            else if (content.ToolCall is { } call)
            {
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments, AepProtocol.JsonOptions) ?? [];
                contents.Add(new FunctionCallContent(call.Id, call.Name, arguments));
            }
            else if (content.ToolResult is { } result)
                contents.Add(new FunctionResultContent(result.CallId, result.Result));
        }
        return contents;
    }

    private static AepRole MapRole(ChatRole role) => role == ChatRole.System ? AepRole.System
        : role == ChatRole.Assistant ? AepRole.Assistant
        : role == ChatRole.Tool ? AepRole.Tool
        : AepRole.User;

    private static ChatRole MapRole(AepRole role) => role == AepRole.System ? ChatRole.System
        : role == AepRole.Assistant ? ChatRole.Assistant
        : role == AepRole.Tool ? ChatRole.Tool
        : ChatRole.User;

    private static ChatRole? MapRole(AepRole? role) => role is null ? null : MapRole(role.Value);

    private static ChatFinishReason? MapFinishReason(AepFinishReason? reason) => reason switch
    {
        AepFinishReason.Stop => ChatFinishReason.Stop,
        AepFinishReason.Length => ChatFinishReason.Length,
        AepFinishReason.ToolCalls => ChatFinishReason.ToolCalls,
        AepFinishReason.ContentFilter => ChatFinishReason.ContentFilter,
        _ => null
    };
}
