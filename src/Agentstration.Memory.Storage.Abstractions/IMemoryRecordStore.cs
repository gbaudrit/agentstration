using Agentstration.Memory;
using Agentstration.Resources;

namespace Agentstration.Memory.Storage.Abstractions;

public interface IMemoryRecordStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task AddAsync(MemoryRecord record, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken);
    Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken);
    Task<int> PurgeExpiredAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);
}
