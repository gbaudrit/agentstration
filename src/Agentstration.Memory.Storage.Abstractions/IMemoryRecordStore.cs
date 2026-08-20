using Agentstration.Memory;
using Agentstration.Resources;

namespace Agentstration.Memory.Storage.Abstractions;

public interface IMemoryRecordStore
{
    /// <summary>Initializes the store idempotently.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
    /// <summary>Adds one immutable record. A duplicate Workspace/id pair must fail.</summary>
    Task AddAsync(MemoryRecord record, CancellationToken cancellationToken);
    /// <summary>Gets a record only inside the supplied Workspace.</summary>
    Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken);
    /// <summary>Lists active records newest-first with exact scope, offset, and limit semantics.</summary>
    Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken);
    /// <summary>Deletes at most one record inside the supplied Workspace.</summary>
    Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken);
    /// <summary>Deletes only records matching the exact Workspace and scope.</summary>
    Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken);
    /// <summary>Deletes at most <paramref name="take"/> expired records inside the supplied Workspace.</summary>
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
