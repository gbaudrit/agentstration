using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Management.Storage.Sqlite;

internal sealed class TriggerOccurrenceRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid TriggerUid { get; set; }
    public string TriggerName { get; set; } = string.Empty;
    public string TriggerNamespace { get; set; } = ResourceNamespace.DefaultValue;
    public long TriggerGeneration { get; set; }
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? FiredAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? WorkItemId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class SqliteTriggerOccurrenceStore(IDbContextFactory<ControlPlaneDbContext> contextFactory) : ITriggerOccurrenceStore
{
    public async Task<bool> TryCreateAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.TriggerOccurrences.Add(ToRow(occurrence));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await context.TriggerOccurrences.AsNoTracking().AnyAsync(value => value.Id == occurrence.Id, cancellationToken)) return false;
            throw;
        }
    }

    public async Task CompleteAsync(Guid workspaceId, Guid occurrenceId, TriggerOccurrenceOutcome outcome, DateTimeOffset firedAt, string? workItemId, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.TriggerOccurrences.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId && value.Id == occurrenceId, cancellationToken)
            ?? throw new InvalidOperationException($"Trigger occurrence '{occurrenceId}' was not found.");
        row.FiredAt = firedAt;
        row.Outcome = outcome.ToString();
        row.WorkItemId = workItemId;
        row.ErrorCode = errorCode;
        row.ErrorMessage = errorMessage;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TriggerOccurrence>> ListAsync(Guid workspaceId, Guid triggerUid, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.TriggerOccurrences.AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId && value.TriggerUid == triggerUid)
            .ToArrayAsync(cancellationToken);
        return rows.OrderByDescending(value => value.ScheduledAt).Take(Math.Clamp(take, 1, 200)).Select(ToModel).ToArray();
    }

    private static TriggerOccurrenceRow ToRow(TriggerOccurrence occurrence) => new()
    {
        Id = occurrence.Id,
        TenantId = occurrence.TenantId,
        WorkspaceId = occurrence.WorkspaceId,
        TriggerUid = occurrence.TriggerUid,
        TriggerName = occurrence.TriggerName,
        TriggerNamespace = occurrence.TriggerNamespace.Value,
        TriggerGeneration = occurrence.TriggerGeneration,
        Kind = occurrence.Kind.ToString(),
        ScheduledAt = occurrence.ScheduledAt,
        FiredAt = occurrence.FiredAt,
        Outcome = occurrence.Outcome.ToString(),
        WorkItemId = occurrence.WorkItemId,
        ErrorCode = occurrence.ErrorCode,
        ErrorMessage = occurrence.ErrorMessage
    };

    private static TriggerOccurrence ToModel(TriggerOccurrenceRow row) => new()
    {
        Id = row.Id,
        TenantId = row.TenantId,
        WorkspaceId = row.WorkspaceId,
        TriggerUid = row.TriggerUid,
        TriggerName = row.TriggerName,
        TriggerNamespace = new(row.TriggerNamespace),
        TriggerGeneration = row.TriggerGeneration,
        Kind = Enum.Parse<TriggerOccurrenceKind>(row.Kind),
        ScheduledAt = row.ScheduledAt,
        FiredAt = row.FiredAt,
        Outcome = Enum.Parse<TriggerOccurrenceOutcome>(row.Outcome),
        WorkItemId = row.WorkItemId,
        ErrorCode = row.ErrorCode,
        ErrorMessage = row.ErrorMessage
    };
}
