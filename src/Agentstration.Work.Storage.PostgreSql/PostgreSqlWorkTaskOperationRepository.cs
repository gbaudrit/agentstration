using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlWorkTaskOperationRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkTaskOperationRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        if (!await context.Database.CanConnectAsync(token)) throw new InvalidOperationException("The PostgreSQL Work store is not accessible.");
    }

    public async Task<WorkTaskOperation?> GetAsync(WorkspaceId workspaceId, Guid principalId, WorkTaskOperationId id, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var key = id.ToString();
        var row = await context.WorkTaskOperations.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspace
            && value.OwnerPrincipalId == owner && value.Id == key, token);
        return row is null ? null : Decode(row.Payload);
    }

    public async Task<WorkTaskOperationPage> ListAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        take = Math.Clamp(take, 1, 100);
        var rows = await context.WorkTaskOperations.AsNoTracking().Where(value => value.WorkspaceId == workspace
            && value.OwnerPrincipalId == owner && value.ProjectId == project).OrderBy(value => value.CreatedAtTicks).ThenBy(value => value.Id)
            .Skip(Math.Max(0, skip)).Take(take + 1).Select(value => value.Payload).ToArrayAsync(token);
        return new(rows.Take(take).Select(Decode).ToArray(), rows.Length > take);
    }

    public async Task<WorkTaskOperationSaveStatus> TryCreateAsync(WorkTaskOperationAdmission admission, CancellationToken token)
    {
        var operation = admission.Operation;
        if (operation.Version != 1 || operation.Status != WorkTaskOperationStatus.Pending || operation.Id.Value == Guid.Empty
            || operation.WorkspaceId.Value == Guid.Empty || operation.OwnerPrincipalId == Guid.Empty
            || operation.ProjectVersion != admission.ProjectVersion || operation.PlanId != admission.Plan?.PlanId)
            throw new ArgumentException("A scoped, guarded Task provider operation is required.", nameof(admission));
        await using var strategyContext = await contextFactory.CreateDbContextAsync(token);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(token);
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var workspace = operation.WorkspaceId.ToString(); var owner = operation.OwnerPrincipalId.ToString("D"); var key = operation.Id.ToString();
            if (await context.WorkTaskOperations.AnyAsync(value => value.WorkspaceId == workspace && value.Id == key, token))
                return WorkTaskOperationSaveStatus.Conflict;
            if (admission.Plan is { } plan)
            {
                var planKey = plan.PlanId.ToString();
                var matched = await context.WorkPlans.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                    && value.Id == planKey && value.Version == plan.Version).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), token);
                if (matched != 1) return WorkTaskOperationSaveStatus.WorkChanged;
            }
            var projectKey = operation.ProjectId.ToString();
            if (await context.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                && value.Id == projectKey && value.Version == admission.ProjectVersion).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), token) != 1)
                return WorkTaskOperationSaveStatus.WorkChanged;
            context.WorkTaskOperations.Add(new WorkTaskOperationDocument { WorkspaceId = workspace, OwnerPrincipalId = owner, Id = key,
                ProjectId = projectKey, Version = operation.Version, CreatedAtTicks = operation.CreatedAt.UtcTicks, Payload = JsonSerializer.Serialize(operation, Json) });
            try
            {
                await context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return WorkTaskOperationSaveStatus.Saved;
            }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            { return WorkTaskOperationSaveStatus.Conflict; }
        });
    }

    public async Task<bool> TryUpdateAsync(WorkTaskOperation operation, long expectedVersion, CancellationToken token)
    {
        if (operation.Version != expectedVersion + 1 || operation.Version < 2) throw new ArgumentException("A versioned operation is required.", nameof(operation));
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = operation.WorkspaceId.ToString(); var owner = operation.OwnerPrincipalId.ToString("D"); var key = operation.Id.ToString();
        var row = await context.WorkTaskOperations.SingleOrDefaultAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
            && value.Id == key && value.Version == expectedVersion, token);
        if (row is null) return false;
        var current = Decode(row.Payload);
        if (current.Status is WorkTaskOperationStatus.Succeeded or WorkTaskOperationStatus.Rejected || current.IntentDigest != operation.IntentDigest
            || current.ProjectId != operation.ProjectId || current.OperationKey != operation.OperationKey
            || JsonSerializer.Serialize(current with { Version = operation.Version, Status = operation.Status, Task = operation.Task,
                ErrorCode = operation.ErrorCode, UpdatedAt = operation.UpdatedAt }, Json) != JsonSerializer.Serialize(operation, Json))
            return false;
        row.Version = operation.Version; row.Payload = JsonSerializer.Serialize(operation, Json);
        try { await context.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private static WorkTaskOperation Decode(string payload) => JsonSerializer.Deserialize<WorkTaskOperation>(payload, Json)
        ?? throw new InvalidOperationException("The Task provider operation payload is invalid.");
}
