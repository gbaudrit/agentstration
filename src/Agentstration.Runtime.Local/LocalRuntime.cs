using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.Local;

public sealed class SingleChatClientResolver(IChatClient chatClient) : IChatClientResolver
{
    public ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelProfileResourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(chatClient);
    }
}

public sealed class EmptyToolCatalog : IToolCatalog
{
    public ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(
        IEnumerable<string> toolIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = toolIds.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 0)
            throw new InvalidOperationException($"No tools are registered in the standalone catalog. Requested: {string.Join(", ", requested)}.");
        return ValueTask.FromResult<IReadOnlyCollection<IAgentTool>>([]);
    }
}
