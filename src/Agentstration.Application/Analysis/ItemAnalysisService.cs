using Agentstration.Domain;

namespace Agentstration.Application.Analysis;

public sealed class ItemAnalysisService(IPlatformStore store) : IItemAnalysisStore
{
    public Task AddAsync(ItemAnalysis analysis, CancellationToken cancellationToken) => store.AddItemAnalysisAsync(analysis, cancellationToken);
}
