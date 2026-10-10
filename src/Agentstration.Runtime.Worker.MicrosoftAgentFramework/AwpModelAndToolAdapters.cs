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
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await session.EnsureCanStartMutationAsync(cancellationToken);
        var request = new AwpInvokeModelRequest(session.Context, agent.ParticipantId, turn.TurnId,
            turn.Attempt.TurnAttemptId, messages.Select(ToContract).ToArray());
        var response = await session.Client.InvokeModelAsync(request, cancellationToken);
        var contents = response.Contents.Select(ToContent).ToList();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { ModelId = response.ModelId };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static AwpModelMessage ToContract(ChatMessage message) => new(
        message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.Tool ? "tool" : "user",
        message.Contents.Select(ToContract).Where(content => content is not null).Cast<AwpModelContent>().ToArray());

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
