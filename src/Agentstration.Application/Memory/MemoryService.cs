using Agentstration.Domain;

namespace Agentstration.Application.Memory;

public sealed class MemoryService(IPlatformStore store) : IMemoryStore, IMemorySearch
{
    public Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken) => store.AddMemoryEntryAsync(entry, cancellationToken);

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(WorkspaceId workspaceId, string query, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        return store.SearchMemoryAsync(workspaceId, query.Trim(), limit, cancellationToken);
    }
}
