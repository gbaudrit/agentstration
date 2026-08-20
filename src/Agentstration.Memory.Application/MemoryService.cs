using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Memory.Application;

public sealed record WriteMemoryCommand(
    WorkspaceId WorkspaceId,
    MemoryScope Scope,
    string Content,
    IReadOnlyList<string> Tags,
    MemorySourceKind SourceKind,
    string? SourceId,
    string Reason,
    Guid PrincipalId,
    DateTimeOffset? ExpiresAt = null);

public sealed record MemoryRetrievalRequest(WorkspaceId WorkspaceId, IReadOnlyList<MemoryScope> Scopes, int Limit);

public interface IMemoryRetriever
{
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(MemoryRetrievalRequest request, CancellationToken cancellationToken);
}

public sealed class MemoryService(IMemoryRecordStore store, TimeProvider timeProvider) : IMemoryRetriever
{
    public Task InitializeAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public async Task<MemoryRecord> WriteAsync(WriteMemoryCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var record = MemoryValidator.Validate(new MemoryRecord(
            MemoryRecordId.New(), command.WorkspaceId, command.Scope, command.Content, command.Tags,
            new MemoryProvenance(command.SourceKind, command.SourceId, command.Reason, command.PrincipalId), now, command.ExpiresAt), now);
        await store.AddAsync(record, cancellationToken);
        return record;
    }

    public Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) =>
        store.GetAsync(workspaceId, id, cancellationToken);

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (scope is not null) MemoryValidator.ValidateScope(scope);
        return store.ListAsync(workspaceId, scope, timeProvider.GetUtcNow(), skip, Math.Clamp(take, 1, MemoryLimits.MaximumAdministrationPageSize), cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(MemoryRetrievalRequest request, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, 1, MemoryLimits.MaximumRetrievalCount);
        var values = new List<MemoryRecord>();
        foreach (var scope in request.Scopes.Distinct())
        {
            MemoryValidator.ValidateScope(scope);
            values.AddRange(await store.ListAsync(request.WorkspaceId, scope, timeProvider.GetUtcNow(), 0, limit, cancellationToken));
        }
        return values.OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id.Value).Take(limit).ToArray();
    }

    public Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) => store.DeleteAsync(workspaceId, id, cancellationToken);
    public Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken)
    {
        MemoryValidator.ValidateScope(scope);
        return store.ClearScopeAsync(workspaceId, scope, cancellationToken);
    }
    public Task<int> PurgeExpiredAsync(int take, CancellationToken cancellationToken) => store.PurgeExpiredAsync(timeProvider.GetUtcNow(), Math.Clamp(take, 1, 1_000), cancellationToken);
}
