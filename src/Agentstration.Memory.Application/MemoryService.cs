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
    DateTimeOffset? ExpiresAt = null,
    MemoryProviderReference? Provider = null);

public sealed record MemoryRetrievalRequest(WorkspaceId WorkspaceId, IReadOnlyList<MemoryScope> Scopes, int Limit, MemoryProviderReference? Provider = null);

public interface IMemoryRetriever
{
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(MemoryRetrievalRequest request, CancellationToken cancellationToken);
}

public sealed class MemoryService : IMemoryRetriever
{
    private readonly IMemoryRecordStoreResolver stores;
    private readonly TimeProvider timeProvider;
    private readonly IMemoryMutationAuditStore? audit;

    public MemoryService(IMemoryRecordStoreResolver stores, TimeProvider timeProvider, IMemoryMutationAuditStore? audit = null)
    {
        this.stores = stores;
        this.timeProvider = timeProvider;
        this.audit = audit;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        await (await stores.ResolveAsync(default, MemoryProviderReference.Local, cancellationToken)).InitializeAsync(cancellationToken);

    public async Task<MemoryRecord> WriteAsync(WriteMemoryCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var provider = command.Provider ?? MemoryProviderReference.Local;
        var operationId = Guid.NewGuid();
        var record = MemoryValidator.Validate(new MemoryRecord(
            MemoryRecordId.New(), command.WorkspaceId, command.Scope, command.Content, command.Tags,
            new MemoryProvenance(command.SourceKind, command.SourceId, command.Reason, command.PrincipalId), now, command.ExpiresAt), now);
        await AuditAsync(operationId, command.WorkspaceId, provider, MemoryMutationOperation.Write, MemoryMutationOutcome.Requested, command.PrincipalId, command.Scope, record.Id, null, command.SourceKind, command.SourceId, null, cancellationToken);
        var store = await ResolveAsync(command.WorkspaceId, provider, cancellationToken);
        try
        {
            await store.AddAsync(record, cancellationToken);
            await AuditAsync(operationId, command.WorkspaceId, provider, MemoryMutationOperation.Write, MemoryMutationOutcome.Succeeded, command.PrincipalId, command.Scope, record.Id, 1, command.SourceKind, command.SourceId, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(operationId, command.WorkspaceId, provider, MemoryMutationOperation.Write, MemoryMutationOutcome.Failed, command.PrincipalId, command.Scope, record.Id, null, command.SourceKind, command.SourceId, exception.GetType().Name, cancellationToken);
            throw;
        }
        return record;
    }

    public async Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken, MemoryProviderReference? provider = null) =>
        await (await ResolveAsync(workspaceId, provider, cancellationToken)).GetAsync(workspaceId, id, cancellationToken);

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, int skip, int take, CancellationToken cancellationToken, MemoryProviderReference? provider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (scope is not null) MemoryValidator.ValidateScope(scope);
        return await (await ResolveAsync(workspaceId, provider, cancellationToken)).ListAsync(workspaceId, scope, timeProvider.GetUtcNow(), skip, Math.Clamp(take, 1, MemoryLimits.MaximumAdministrationPageSize), cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(MemoryRetrievalRequest request, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, 1, MemoryLimits.MaximumRetrievalCount);
        var store = await ResolveAsync(request.WorkspaceId, request.Provider, cancellationToken);
        var values = new List<MemoryRecord>();
        foreach (var scope in request.Scopes.Distinct())
        {
            MemoryValidator.ValidateScope(scope);
            values.AddRange(await store.ListAsync(request.WorkspaceId, scope, timeProvider.GetUtcNow(), 0, limit, cancellationToken));
        }
        return values.OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id.Value).Take(limit).ToArray();
    }

    public async Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken, MemoryProviderReference? provider = null, Guid? principalId = null)
    {
        var actualProvider = provider ?? MemoryProviderReference.Local;
        var operationId = Guid.NewGuid();
        await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.Delete, MemoryMutationOutcome.Requested, principalId, null, id, null, null, null, null, cancellationToken);
        try
        {
            var deleted = await (await ResolveAsync(workspaceId, actualProvider, cancellationToken)).DeleteAsync(workspaceId, id, cancellationToken);
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.Delete, MemoryMutationOutcome.Succeeded, principalId, null, id, deleted ? 1 : 0, null, null, null, cancellationToken);
            return deleted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.Delete, MemoryMutationOutcome.Failed, principalId, null, id, null, null, null, exception.GetType().Name, cancellationToken);
            throw;
        }
    }
    public async Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken, MemoryProviderReference? provider = null, Guid? principalId = null)
    {
        MemoryValidator.ValidateScope(scope);
        var actualProvider = provider ?? MemoryProviderReference.Local;
        var operationId = Guid.NewGuid();
        await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.ClearScope, MemoryMutationOutcome.Requested, principalId, scope, null, null, null, null, null, cancellationToken);
        try
        {
            var affected = await (await ResolveAsync(workspaceId, actualProvider, cancellationToken)).ClearScopeAsync(workspaceId, scope, cancellationToken);
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.ClearScope, MemoryMutationOutcome.Succeeded, principalId, scope, null, affected, null, null, null, cancellationToken);
            return affected;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.ClearScope, MemoryMutationOutcome.Failed, principalId, scope, null, null, null, null, exception.GetType().Name, cancellationToken);
            throw;
        }
    }
    public async Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, int take, CancellationToken cancellationToken, MemoryProviderReference? provider = null, Guid? principalId = null)
    {
        var actualProvider = provider ?? MemoryProviderReference.Local;
        var operationId = Guid.NewGuid();
        await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.PurgeExpired, MemoryMutationOutcome.Requested, principalId, null, null, null, null, null, null, cancellationToken);
        try
        {
            var affected = await (await ResolveAsync(workspaceId, actualProvider, cancellationToken)).PurgeExpiredAsync(workspaceId, timeProvider.GetUtcNow(), Math.Clamp(take, 1, 1_000), cancellationToken);
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.PurgeExpired, MemoryMutationOutcome.Succeeded, principalId, null, null, affected, null, null, null, cancellationToken);
            return affected;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(operationId, workspaceId, actualProvider, MemoryMutationOperation.PurgeExpired, MemoryMutationOutcome.Failed, principalId, null, null, null, null, null, exception.GetType().Name, cancellationToken);
            throw;
        }
    }

    private ValueTask<IMemoryRecordStore> ResolveAsync(WorkspaceId workspaceId, MemoryProviderReference? provider, CancellationToken cancellationToken) =>
        stores.ResolveAsync(workspaceId, provider ?? MemoryProviderReference.Local, cancellationToken);

    public Task<IReadOnlyList<MemoryMutationAuditRecord>> ListAuditAsync(WorkspaceId workspaceId, MemoryProviderReference provider, int skip, int take, CancellationToken cancellationToken) =>
        audit is null ? Task.FromResult<IReadOnlyList<MemoryMutationAuditRecord>>([]) : audit.ListAsync(workspaceId, provider, Math.Max(0, skip), Math.Clamp(take, 1, 200), cancellationToken);

    private Task AuditAsync(Guid operationId, WorkspaceId workspaceId, MemoryProviderReference provider, MemoryMutationOperation operation, MemoryMutationOutcome outcome,
        Guid? principalId, MemoryScope? scope, MemoryRecordId? recordId, int? affected, MemorySourceKind? sourceKind, string? sourceId, string? errorCode, CancellationToken cancellationToken) =>
        audit is null ? Task.CompletedTask : audit.AppendAsync(new(Guid.NewGuid(), operationId, workspaceId, provider, operation, outcome,
            timeProvider.GetUtcNow(), principalId, scope, recordId, affected, sourceKind, sourceId, errorCode), cancellationToken);
}
