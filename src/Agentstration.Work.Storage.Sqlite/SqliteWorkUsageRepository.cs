using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class SqliteWorkUsageRepository(IDbContextFactory<WorkDbContext> factory) : IWorkUsageRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.EnsureCreatedAsync(token);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorkUsageRecords (WorkspaceId TEXT NOT NULL, Id TEXT NOT NULL, OwnerPrincipalId TEXT NOT NULL, ProjectId TEXT NOT NULL, RootId TEXT NOT NULL, PlanId TEXT NOT NULL, TaskId TEXT NOT NULL, AttemptId TEXT NOT NULL, Revision INTEGER NOT NULL, CreatedAtTicks INTEGER NOT NULL, Digest TEXT NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY (WorkspaceId, Id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkUsageRecords_RootRevision ON WorkUsageRecords (WorkspaceId, OwnerPrincipalId, ProjectId, RootId, Revision);
            CREATE INDEX IF NOT EXISTS IX_WorkUsageRecords_History ON WorkUsageRecords (WorkspaceId, OwnerPrincipalId, ProjectId, CreatedAtTicks, Id);
            CREATE INDEX IF NOT EXISTS IX_WorkUsageRecords_Plan ON WorkUsageRecords (WorkspaceId, OwnerPrincipalId, ProjectId, PlanId);
            CREATE INDEX IF NOT EXISTS IX_WorkUsageRecords_Task ON WorkUsageRecords (WorkspaceId, OwnerPrincipalId, ProjectId, TaskId);
            CREATE INDEX IF NOT EXISTS IX_WorkUsageRecords_Attempt ON WorkUsageRecords (WorkspaceId, OwnerPrincipalId, ProjectId, AttemptId);
            """, token);
    }

    public async Task<WorkUsageEntry?> GetAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkUsageId id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var key = id.ToString();
        var row = await Owned(db, workspaceId, principalId, projectId).SingleOrDefaultAsync(value => value.Id == key, token);
        return row is null ? null : Decode(row);
    }

    public async Task<WorkUsagePage> ListAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await Owned(db, workspaceId, principalId, projectId).OrderByDescending(value => value.CreatedAtTicks).ThenBy(value => value.Id)
            .Skip(skip).Take(take + 1).ToArrayAsync(token);
        return new(rows.Take(take).Select(Decode).ToArray(), rows.Length > take);
    }

    public async Task<IReadOnlyList<WorkUsageEntry>> ReadProjectAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        IReadOnlyList<WorkPlanId> planIds, int maximumRecords, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var query = Owned(db, workspaceId, principalId, projectId);
        if (planIds.Count > 0)
        {
            var plans = planIds.Select(id => id.ToString()).ToArray();
            query = query.Where(value => plans.Contains(value.PlanId));
        }
        return (await query.OrderBy(value => value.RootId).ThenBy(value => value.Revision).Take(maximumRecords).ToArrayAsync(token)).Select(Decode).ToArray();
    }

    public async Task<WorkUsageSaveStatus> CaptureAsync(IReadOnlyList<WorkUsageEntry> originals, CancellationToken token)
    {
        if (originals.Count is < 1 or > 32 || originals.Select(value => value.Record.Id).Distinct().Count() != originals.Count) throw new ArgumentException("A bounded usage capture batch is required.", nameof(originals));
        var scope = originals[0];
        foreach (var entry in originals)
            if (entry.WorkspaceId != scope.WorkspaceId || entry.PrincipalId != scope.PrincipalId || entry.ProjectId != scope.ProjectId
                || entry.Record.Revision != 1 || !Valid(entry)) throw new ArgumentException("Usage capture identities or content are invalid.", nameof(originals));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            if (!await LockProjectAsync(db, scope, token)) return WorkUsageSaveStatus.Conflict;
            var changed = false;
            foreach (var entry in originals)
            {
                var key = entry.Record.Id.ToString();
                var existing = await Owned(db, scope.WorkspaceId, scope.PrincipalId, scope.ProjectId).SingleOrDefaultAsync(value => value.Id == key, token);
                if (existing is not null)
                {
                    if (existing.Digest != entry.Digest) return WorkUsageSaveStatus.Conflict;
                    continue;
                }
                db.WorkUsageRecords.Add(Encode(entry)); changed = true;
            }
            try
            {
                await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                return changed ? WorkUsageSaveStatus.Saved : WorkUsageSaveStatus.Replayed;
            }
            catch (DbUpdateConcurrencyException) { return WorkUsageSaveStatus.Conflict; }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 }) { return WorkUsageSaveStatus.Conflict; }
        });
    }

    public async Task<WorkUsageSaveStatus> AppendCorrectionAsync(WorkUsageEntry entry, CancellationToken token)
    {
        if (!Valid(entry) || entry.Record.Revision < 2) throw new ArgumentException("A versioned usage correction is required.", nameof(entry));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            if (!await LockProjectAsync(db, entry, token)) return WorkUsageSaveStatus.Conflict;
            var key = entry.Record.Id.ToString(); var root = entry.Record.RootId.ToString();
            var owned = Owned(db, entry.WorkspaceId, entry.PrincipalId, entry.ProjectId);
            var existing = await owned.SingleOrDefaultAsync(value => value.Id == key, token);
            if (existing is not null) return existing.Digest == entry.Digest ? WorkUsageSaveStatus.Replayed : WorkUsageSaveStatus.Conflict;
            var previous = await owned.Where(value => value.RootId == root).OrderByDescending(value => value.Revision).FirstOrDefaultAsync(token);
            if (previous is null || previous.Id != entry.Record.PreviousId!.Value.ToString() || previous.Digest != entry.Record.PreviousDigest
                || previous.Revision != entry.Record.Revision - 1 || !WorkUsageAccounting.SameIdentity(Decode(previous).Record, entry.Record)) return WorkUsageSaveStatus.Conflict;
            db.WorkUsageRecords.Add(Encode(entry));
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return WorkUsageSaveStatus.Saved; }
            catch (DbUpdateConcurrencyException) { return WorkUsageSaveStatus.Conflict; }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 }) { return WorkUsageSaveStatus.Conflict; }
        });
    }

    private static IQueryable<WorkUsageDocument> Owned(WorkDbContext db, WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId)
    {
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        return db.WorkUsageRecords.AsNoTracking().Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.ProjectId == project);
    }
    private static async Task<bool> LockProjectAsync(WorkDbContext db, WorkUsageEntry entry, CancellationToken token)
    {
        var workspace = entry.WorkspaceId.ToString(); var owner = entry.PrincipalId.ToString("D"); var project = entry.ProjectId.ToString();
        return await db.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == project)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) == 1;
    }
    private static bool Valid(WorkUsageEntry entry) => entry.WorkspaceId.Value != Guid.Empty && entry.PrincipalId != Guid.Empty && entry.ProjectId.Value != Guid.Empty
        && WorkUsageFormat.Valid(entry.Record) && entry.Digest == WorkUsageFormat.Digest(entry.Record);
    private static WorkUsageEntry Decode(WorkUsageDocument value) => JsonSerializer.Deserialize<WorkUsageEntry>(value.Payload, Json)
        ?? throw new InvalidOperationException("The persisted usage record is invalid.");
    private static WorkUsageDocument Encode(WorkUsageEntry value) => new()
    {
        WorkspaceId = value.WorkspaceId.ToString(),
        Id = value.Record.Id.ToString(),
        OwnerPrincipalId = value.PrincipalId.ToString("D"),
        ProjectId = value.ProjectId.ToString(),
        RootId = value.Record.RootId.ToString(),
        PlanId = value.Record.PlanId.ToString(),
        TaskId = value.Record.TaskId.ToString(),
        AttemptId = value.Record.Source.AttemptId.ToString("D"),
        Revision = value.Record.Revision,
        CreatedAtTicks = value.CreatedAt.UtcTicks,
        Digest = value.Digest,
        Payload = JsonSerializer.Serialize(value, Json)
    };
}
