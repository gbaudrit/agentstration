using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;

namespace Agentstration.Infrastructure.Agents;

public sealed partial class DeterministicChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var materialized = messages.ToArray();
        var content = materialized.LastOrDefault()?.Text ?? string.Empty;
        if (materialized.Any(message => message.Text.Contains("AGENTSTRATION_ROUTER", StringComparison.Ordinal)) || IsRoutingPayload(content))
        {
            return Task.FromResult(Route(content));
        }
        var words = Words().Matches(content).Select(match => match.Value).ToArray();
        var summary = string.Join(' ', words.Take(40));
        if (words.Length > 40) summary += "…";
        if (string.IsNullOrWhiteSpace(summary)) summary = "No textual content.";

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary)));
    }

    private static ChatResponse Route(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var request = document.RootElement.GetProperty("request").GetString() ?? string.Empty;
        var requestWords = Words().Matches(request).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = document.RootElement.GetProperty("candidates").EnumerateArray()
            .Select(candidate =>
            {
                var agentId = candidate.GetProperty("agentId").GetString() ?? string.Empty;
                var description = candidate.GetProperty("description").GetString() ?? string.Empty;
                var capabilities = candidate.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString() ?? string.Empty);
                var searchable = string.Join(' ', capabilities.Prepend(description).Prepend(agentId));
                var score = Words().Matches(searchable).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(requestWords.Contains);
                return (AgentId: agentId, Score: score);
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.AgentId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No routing candidates were supplied.");
        var selected = candidates[0];
        var confidence = selected.Score == 0 ? 0.5 : Math.Min(0.99, 0.7 + (selected.Score * 0.05));
        var result = new Agentstration.Runtime.Abstractions.AgentRouteResult(selected.AgentId, confidence, selected.Score == 0 ? "Deterministic fallback." : "Best deterministic capability match.");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(result)));
    }

    private static bool IsRoutingPayload(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("request", out _)
                && document.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-'’]*")]
    private static partial Regex Words();
}
