using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpChatClientResolver(AwpModelChatClient client) : IChatClientResolver
{
    public ValueTask<IChatClient> ResolveAsync(string modelProfileName, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IChatClient>(client);

    public ValueTask<IChatClient> ResolveAsync(ResourceNamespace @namespace, string modelProfileName,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<IChatClient>(client);
}

internal sealed class AwpModelChatClient(
    AwpAssignmentSession session,
    AwpExecutionAgentMaterial agent,
    AwpAgentTurnLocation turn) : IChatClient
{
    private string? responseModelId;
    private string? responseProviderType;
    public string? LastModelId => responseModelId;
    public string? LastProviderType => responseProviderType;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await session.EnsureCanStartMutationAsync(cancellationToken);
        var request = new AwpInvokeModelRequest(session.Context, agent.ParticipantId, turn.TurnId,
            turn.Attempt.TurnAttemptId, messages.Select(ToContract).ToArray(), ToContract(options));
        var response = await session.Client.InvokeModelAsync(request, cancellationToken);
        responseModelId = response.ModelId;
        responseProviderType = response.ProviderType;
        var contents = response.Contents.Select(ToContent).ToList();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = response.ModelId,
            Usage = response.InputTokens is null && response.OutputTokens is null ? null : new UsageDetails
            {
                InputTokenCount = response.InputTokens,
                OutputTokenCount = response.OutputTokens
            }
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await session.EnsureCanStartMutationAsync(cancellationToken);
        var request = new AwpInvokeModelRequest(session.Context, agent.ParticipantId, turn.TurnId,
            turn.Attempt.TurnAttemptId, messages.Select(ToContract).ToArray(), ToContract(options));
        var calls = new Dictionary<string, ToolCallBuffer>(StringComparer.Ordinal);
        await foreach (var streamEvent in session.Client.StreamModelAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case AwpModelStreamStarted:
                    break;
                case AwpModelTextDelta text:
                    yield return Update(text.StreamId, text.ModelId,
                        [new TextContent(text.Text)]);
                    break;
                case AwpModelToolCallStarted started:
                    if (!calls.TryAdd(started.CallId, new(started.Name)))
                        throw InvalidStream("The AWP model stream contains a duplicate Tool call.");
                    break;
                case AwpModelToolCallArgumentsDelta arguments:
                    if (!calls.TryGetValue(arguments.CallId, out var call))
                        throw InvalidStream("The AWP model stream contains Tool arguments before Tool-call start.");
                    call.Arguments.Append(arguments.Delta);
                    if (call.Arguments.Length > 64 * 1024)
                        throw InvalidStream("The AWP model stream Tool arguments exceed their size limit.");
                    break;
                case AwpModelToolCallDone done:
                    if (!calls.Remove(done.CallId, out var completedCall))
                        throw InvalidStream("The AWP model stream completes an unknown Tool call.");
                    yield return Update(done.StreamId, null,
                        [new FunctionCallContent(done.CallId, completedCall.Name, ParseArguments(completedCall.Arguments))]);
                    break;
                case AwpModelToolResult result:
                    yield return Update(result.StreamId, null,
                        [new FunctionResultContent(result.CallId, result.Result.Deserialize<object?>())]);
                    break;
                case AwpModelUsage usage:
                    yield return Update(usage.StreamId, null,
                        [new UsageContent(new UsageDetails
                        {
                            InputTokenCount = usage.InputTokens,
                            OutputTokenCount = usage.OutputTokens
                        })]);
                    break;
                case AwpModelStreamCompleted completed:
                    if (calls.Count != 0)
                        throw InvalidStream("The AWP model stream ended with incomplete Tool calls.");
                    responseModelId = completed.ModelId;
                    responseProviderType = completed.ProviderType;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [])
                    {
                        ResponseId = completed.StreamId.ToString("D"),
                        ModelId = completed.ModelId,
                        FinishReason = completed.FinishReason is null ? null : new(completed.FinishReason)
                    };
                    break;
                case AwpModelStreamFailed failed:
                    throw new AwpModelStreamException(failed.Code, failed.Message);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static AwpModelMessage ToContract(ChatMessage message) => new(
        message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.Tool ? "tool" : "user",
        message.Contents.Select(ToContract).Where(content => content is not null).Cast<AwpModelContent>().ToArray());

    private static AwpModelTool[] ToContract(ChatOptions? options) => options?.Tools?
        .OfType<AIFunctionDeclaration>()
        .Select(tool => new AwpModelTool(tool.Name, tool.Description, tool.JsonSchema.Clone()))
        .ToArray() ?? [];

    private static AwpModelContent? ToContract(AIContent content) => content switch
    {
        TextContent text => new AwpModelTextContent(text.Text),
        FunctionCallContent call => new AwpModelToolCallContent(call.CallId, call.Name,
            JsonSerializer.SerializeToElement(call.Arguments)),
        FunctionResultContent result => new AwpModelToolResultContent(result.CallId,
            JsonSerializer.SerializeToElement(result.Result)),
        _ => null
    };

    private static AIContent ToContent(AwpModelContent content) => content switch
    {
        AwpModelTextContent text => new TextContent(text.Text),
        AwpModelToolCallContent call => new FunctionCallContent(call.CallId, call.Name,
            call.Arguments.Deserialize<Dictionary<string, object?>>() ?? new Dictionary<string, object?>()),
        AwpModelToolResultContent result => new FunctionResultContent(result.CallId,
            result.Result.Deserialize<object?>()),
        _ => throw new ArgumentOutOfRangeException(nameof(content))
    };

    private static ChatResponseUpdate Update(Guid streamId, string? modelId, IList<AIContent> contents) => new(
        ChatRole.Assistant, contents)
    {
        ResponseId = streamId.ToString("D"),
        ModelId = modelId
    };

    private static Dictionary<string, object?> ParseArguments(StringBuilder arguments)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.ToString())
                ?? throw InvalidStream("The AWP model stream returned empty Tool arguments.");
        }
        catch (JsonException exception)
        {
            throw new AwpModelStreamException("invalid_response",
                "The AWP model stream returned invalid Tool arguments.", exception);
        }
    }

    private static AwpModelStreamException InvalidStream(string message) =>
        new("invalid_response", message);

    private sealed record ToolCallBuffer(string Name)
    {
        public StringBuilder Arguments { get; } = new();
    }
}

internal sealed record AwpAgentTool(AwpExecutionToolMaterial Material) : IAgentTool
{
    public string Id => Material.Id;
    public string Name => Material.Name;
    public string? Description => Material.Description;
    public string? ProviderId => null;
    public ResourceNamespace? Namespace => ResourceNamespace.Parse(Material.Namespace);
    public ResourceNamespace? ProviderNamespace => null;
    public string? ExternalId => null;
    public JsonElement InputSchema => Material.InputSchema;
    public JsonElement? OutputSchema => Material.OutputSchema;
    public bool RequiresApproval => Material.RequiresApproval;
}

internal sealed class AwpToolCatalog(IReadOnlyList<AwpExecutionToolMaterial> tools) : IToolCatalog
{
    private readonly IReadOnlyDictionary<string, IAgentTool> byId = tools
        .ToDictionary(tool => tool.Id, tool => (IAgentTool)new AwpAgentTool(tool), StringComparer.Ordinal);

    public ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyCollection<IAgentTool>>(toolIds.Select(id =>
            byId.TryGetValue(id, out var tool) ? tool : throw new InvalidOperationException($"Tool '{id}' is absent from the assigned material."))
            .ToArray());
    }
}

internal sealed class AwpToolExecutionPipeline(
    AwpAssignmentSession session,
    AwpExecutionAgentMaterial agent,
    AwpAgentTurnLocation turn) : IToolExecutionPipeline
{
    public async ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await session.EnsureCanStartMutationAsync(cancellationToken);
        var toolCallId = StableGuid(context.ToolCallId);
        var response = await session.Client.InvokeToolAsync(new AwpInvokeToolRequest(
            session.Context,
            agent.ParticipantId,
            turn.TurnId,
            turn.Attempt.TurnAttemptId,
            new(toolCallId),
            context.ToolId,
            context.Arguments), cancellationToken);
        return response.Result?.Clone();
    }

    private static Guid StableGuid(string value)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty) return parsed;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return new Guid(hash[..16]);
    }
}
