using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlWorkTaskDeliveryRepository(IDbContextFactory<WorkDbContext> factory) : IWorkTaskDeliveryRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Database.CanConnectAsync(token)) throw new InvalidOperationException("The PostgreSQL Work store is not accessible.");
    }
    public async Task<WorkTaskDeliveryOperation?> GetAsync(WorkspaceId workspace, Guid principal, WorkProjectId project, WorkTaskDeliveryId id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token); var key = id.ToString();
        var row = await Owned(db, workspace, principal, project).AsNoTracking().SingleOrDefaultAsync(value => value.Id == key, token);
        return row is null ? null : Decode(row);
    }
    public async Task<WorkTaskDeliveryOperation?> GetAttemptAsync(WorkspaceId workspace, Guid principal, WorkProjectId project, Guid attemptId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token); var key = attemptId.ToString("D");
        var row = await Owned(db, workspace, principal, project).AsNoTracking().SingleOrDefaultAsync(value => value.AttemptId == key, token);
        return row is null ? null : Decode(row);
    }
    public async Task<WorkTaskDeliveryPage> ListAsync(WorkspaceId workspace, Guid principal, WorkProjectId project, int skip, int take, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await Owned(db, workspace, principal, project).AsNoTracking().OrderByDescending(value => value.CreatedAtTicks).ThenBy(value => value.Id)
            .Skip(skip).Take(take + 1).ToArrayAsync(token);
        return new(rows.Take(take).Select(Decode).ToArray(), rows.Length > take);
    }
    public async Task<WorkTaskDeliverySaveStatus> AdmitAsync(WorkTaskDeliveryOperation operation, CancellationToken token)
    {
        if (!WorkTaskDeliveryFormat.Valid(operation) || operation.Version != 1 || operation.Stage != WorkTaskDeliveryStage.Prepared
            || operation.Status != WorkTaskDeliveryStatus.Pending || operation.Code is not null || operation.PullRequest is not null
            || operation.ContributionId is not null || operation.EvidenceId is not null || operation.Usage.Count != 0)
            throw new ArgumentException("An immutable, scoped delivery intent is required.", nameof(operation));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token); await using var transaction = await db.Database.BeginTransactionAsync(token);
            var workspace = operation.WorkspaceId.ToString(); var owner = operation.OwnerPrincipalId.ToString("D"); var project = operation.ProjectId.ToString();
            var key = operation.Id.ToString(); var attempt = operation.Intent.Source.AttemptId.ToString("D");
            var existing = await Owned(db, operation.WorkspaceId, operation.OwnerPrincipalId, operation.ProjectId).AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == key || value.AttemptId == attempt, token);
            if (existing is not null) return existing.Id == key && existing.IntentDigest == operation.IntentDigest
                ? WorkTaskDeliverySaveStatus.Replayed : WorkTaskDeliverySaveStatus.Conflict;
            if (await db.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == project && value.Version == operation.ProjectVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return WorkTaskDeliverySaveStatus.WorkChanged;
            var plan = operation.Intent.PlanId.ToString();
            if (await db.WorkPlans.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == plan && value.Version == operation.Intent.PlanVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return WorkTaskDeliverySaveStatus.WorkChanged;
            var task = operation.Intent.TaskId.ToString();
            if (await db.WorkItems.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == task && value.Version == operation.Intent.TaskVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return WorkTaskDeliverySaveStatus.WorkChanged;
            db.WorkTaskDeliveries.Add(new()
            {
                WorkspaceId = workspace,
                Id = key,
                OwnerPrincipalId = owner,
                ProjectId = project,
                PlanId = plan,
                TaskId = task,
                AttemptId = attempt,
                IntentDigest = operation.IntentDigest,
                Version = 1,
                CreatedAtTicks = operation.CreatedAt.UtcTicks,
                Payload = JsonSerializer.Serialize(operation, Json)
            });
            try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return WorkTaskDeliverySaveStatus.Saved; }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return WorkTaskDeliverySaveStatus.Conflict; }
        });
    }
    public async Task<bool> UpdateAsync(WorkTaskDeliveryOperation operation, long expectedVersion, CancellationToken token)
    {
        if (!WorkTaskDeliveryFormat.Valid(operation) || operation.Version != expectedVersion + 1) return false;
        await using var db = await factory.CreateDbContextAsync(token); var key = operation.Id.ToString();
        var row = await Owned(db, operation.WorkspaceId, operation.OwnerPrincipalId, operation.ProjectId)
            .SingleOrDefaultAsync(value => value.Id == key && value.Version == expectedVersion, token);
        if (row is null || !WorkTaskDeliveryFormat.Preserves(Decode(row), operation)) return false;
        row.Version = operation.Version; row.Payload = JsonSerializer.Serialize(operation, Json);
        try { return await db.SaveChangesAsync(token) == 1; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private static IQueryable<WorkTaskDeliveryDocument> Owned(WorkDbContext db, WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId)
    {
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        return db.WorkTaskDeliveries.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.ProjectId == project);
    }
    private static WorkTaskDeliveryOperation Decode(WorkTaskDeliveryDocument row) => JsonSerializer.Deserialize<WorkTaskDeliveryOperation>(row.Payload, Json)
        ?? throw new InvalidOperationException("The persisted delivery operation is invalid.");
}
