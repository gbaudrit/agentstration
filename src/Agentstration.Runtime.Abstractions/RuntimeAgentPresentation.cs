namespace Agentstration.Runtime.Abstractions;

public interface IRuntimeAgentPresentationEnricher
{
    Task<RuntimeAgentPresentation> EnrichAsync(RuntimeAgentPresentation presentation, CancellationToken cancellationToken);
}

public sealed record RuntimeAgentPresentation(string Instructions, IReadOnlyCollection<IAgentTool> Tools)
{
    public static async Task<RuntimeAgentPresentation> CreateAsync(string instructions, IReadOnlyCollection<IAgentTool> tools,
        IEnumerable<IRuntimeAgentPresentationEnricher>? enrichers, CancellationToken cancellationToken)
    {
        var result = new RuntimeAgentPresentation(instructions, tools);
        foreach (var enricher in enrichers ?? []) result = await enricher.EnrichAsync(result, cancellationToken);
        return result;
    }
}
