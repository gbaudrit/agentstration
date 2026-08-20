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
    Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, int take, CancellationToken cancellationToken);
}

public interface IMemoryRecordStoreResolver
{
    ValueTask<IMemoryRecordStore> ResolveAsync(WorkspaceId workspaceId, MemoryProviderReference provider, CancellationToken cancellationToken);
}

public interface IMemoryMutationAuditStore
{
    Task AppendAsync(MemoryMutationAuditRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryMutationAuditRecord>> ListAsync(WorkspaceId workspaceId, MemoryProviderReference provider, int skip, int take, CancellationToken cancellationToken);
}

public sealed class SingleMemoryRecordStoreResolver(IMemoryRecordStore store) : IMemoryRecordStoreResolver
{
    public ValueTask<IMemoryRecordStore> ResolveAsync(WorkspaceId workspaceId, MemoryProviderReference provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ValueTask.FromResult(store);
    }
}
