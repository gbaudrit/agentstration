using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlWorkProjectSharingRepository(IDbContextFactory<WorkDbContext> factory) : IWorkProjectSharingRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Database.CanConnectAsync(token)) throw new InvalidOperationException("The PostgreSQL Work store is not accessible.");
    }
    public async Task<WorkProjectSharingState?> GetStateAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        var row = await db.WorkSharingStates.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project, token);
        return row is null ? null : Decode<WorkProjectSharingState>(row.Payload);
    }
    public async Task<bool> SaveStateAsync(WorkProjectSharingState state, long expectedVersion, CancellationToken token)
    {
        if (state.Version != expectedVersion + 1 || state.Version < 1) throw new ArgumentException("A versioned sharing state is required.", nameof(state));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var workspace = state.WorkspaceId.ToString(); var owner = state.PrincipalId.ToString("D"); var project = state.ProjectId.ToString();
            if (await db.WorkProjects.Where(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.Id == project && x.Version == state.ProjectVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Version, x => x.Version), token) != 1) return false;
            var row = await db.WorkSharingStates.SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project, token);
            if (row is null ? expectedVersion != 0 : row.Version != expectedVersion) return false;
            if (row is null) db.WorkSharingStates.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner, ProjectId = project, Version = state.Version, Payload = Encode(state) });
            else { row.Version = state.Version; row.Payload = Encode(state); }
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { return false; }
        });
    }
    public async Task<WorkSharingOperation?> GetOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkSharingOperationId operationId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString(); var id = operationId.ToString();
        var row = await db.WorkSharingOperations.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project && x.Id == id, token);
        return row is null ? null : Decode<WorkSharingOperation>(row.Payload);
    }
    public async Task<WorkSharingSaveStatus> AdmitAsync(WorkSharingOperation operation, CancellationToken token)
    {
        if (operation.Version != 1 || operation.Status != WorkSharingOperationStatus.Pending || operation.Id.Value == Guid.Empty
            || operation.WorkspaceId.Value == Guid.Empty || operation.PrincipalId == Guid.Empty || operation.Attempts.Count != 0)
            throw new ArgumentException("A scoped sharing intent is required.", nameof(operation));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var workspace = operation.WorkspaceId.ToString(); var owner = operation.PrincipalId.ToString("D"); var project = operation.ProjectId.ToString(); var id = operation.Id.ToString();
            if (await db.WorkSharingOperations.AnyAsync(x => x.WorkspaceId == workspace && x.Id == id, token)) return WorkSharingSaveStatus.Conflict;
            foreach (var plan in operation.Plans.OrderBy(x => x.PlanId.Value))
            {
                var key = plan.PlanId.ToString();
                if (await db.WorkPlans.Where(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.Id == key && x.Version == plan.Version)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Version, x => x.Version), token) != 1) return WorkSharingSaveStatus.WorkChanged;
                var mapping = await db.WorkSharingPlanMappings.SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.PlanId == key, token);
                if (mapping is not null && mapping.ProjectId != project) return WorkSharingSaveStatus.Conflict;
                if (mapping is null) db.WorkSharingPlanMappings.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner, ProjectId = project, PlanId = key });
            }
            if (await db.WorkProjects.Where(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.Id == project && x.Version == operation.ProjectVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Version, x => x.Version), token) != 1) return WorkSharingSaveStatus.WorkChanged;
            db.WorkSharingOperations.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner, ProjectId = project, Id = id,
                Version = operation.Version, CreatedAtTicks = operation.CreatedAt.UtcTicks, Payload = Encode(operation) });
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return WorkSharingSaveStatus.Saved; }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { return WorkSharingSaveStatus.Conflict; }
        });
    }
    public async Task<bool> UpdateOperationAsync(WorkSharingOperation operation, long expectedVersion, CancellationToken token)
    {
        if (operation.Version != expectedVersion + 1 || operation.Version < 2) throw new ArgumentException("A versioned sharing operation is required.", nameof(operation));
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = operation.WorkspaceId.ToString(); var owner = operation.PrincipalId.ToString("D"); var project = operation.ProjectId.ToString(); var id = operation.Id.ToString();
        var row = await db.WorkSharingOperations.SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project && x.Id == id && x.Version == expectedVersion, token);
        if (row is null) return false;
        var before = Decode<WorkSharingOperation>(row.Payload);
        if (before.Status is WorkSharingOperationStatus.Succeeded or WorkSharingOperationStatus.Rejected or WorkSharingOperationStatus.Conflict
            || operation.Attempts.Count < before.Attempts.Count || operation.Attempts.Count > 8
            || Encode(before.Attempts) != Encode(operation.Attempts.Take(before.Attempts.Count).ToArray())
            || Encode(before with { Version = operation.Version, Status = operation.Status, UpdatedAt = operation.UpdatedAt,
                Attempts = operation.Attempts, RemoteRevision = operation.RemoteRevision, ErrorCode = operation.ErrorCode }) != Encode(operation)) return false;
        row.Version = operation.Version; row.Payload = Encode(operation);
        try { await db.SaveChangesAsync(token); return true; } catch (DbUpdateConcurrencyException) { return false; }
    }
    public async Task<IReadOnlyList<WorkSharingOperation>> ListOperationsAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, int skip, int take, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        var rows = await db.WorkSharingOperations.AsNoTracking().Where(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project)
            .OrderBy(x => x.CreatedAtTicks).ThenBy(x => x.Id).Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, 101)).Select(x => x.Payload).ToArrayAsync(token);
        return rows.Select(Decode<WorkSharingOperation>).ToArray();
    }
    public async Task<WorkProjectId?> FindPlanProjectAsync(WorkspaceId workspaceId, Guid principalId, WorkPlanId planId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var plan = planId.ToString();
        var row = await db.WorkSharingPlanMappings.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.PlanId == plan, token);
        return row is null ? null : new WorkProjectId(Guid.Parse(row.ProjectId));
    }
    public async Task<WorkTaskOperationId?> GetPlanOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString(); var plan = planId.ToString();
        var row = await db.WorkSharingPlanMappings.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project && x.PlanId == plan, token);
        return row?.OperationId is { } id ? new WorkTaskOperationId(Guid.Parse(id)) : null;
    }
    public async Task<bool> SetPlanOperationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId, WorkTaskOperationId operationId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString(); var plan = planId.ToString(); var key = operationId.ToString();
        var row = await db.WorkSharingPlanMappings.SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project && x.PlanId == plan, token);
        if (row is null) return false;
        var document = await db.WorkTaskOperations.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.ProjectId == project && x.Id == key, token);
        if (document is null || Decode<WorkTaskOperation>(document.Payload) is not { Status: WorkTaskOperationStatus.Succeeded } operation || operation.PlanId != planId) return false;
        row.OperationId = key; await db.SaveChangesAsync(token); return true;
    }
    public async Task<bool> MapPlanAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, WorkPlanId planId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString(); var plan = planId.ToString();
        if (!await db.WorkProjects.AnyAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.Id == project, token)
            || !await db.WorkPlans.AnyAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.Id == plan, token)) return false;
        var row = await db.WorkSharingPlanMappings.SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.OwnerPrincipalId == owner && x.PlanId == plan, token);
        if (row is not null) return row.ProjectId == project;
        db.WorkSharingPlanMappings.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner, ProjectId = project, PlanId = plan });
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { return false; }
    }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidOperationException("Invalid persisted sharing payload.");
}
