using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlWorkDataRepository(IDbContextFactory<WorkDbContext> factory) : IWorkDataRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Database.CanConnectAsync(token)) throw new InvalidOperationException("The PostgreSQL Work store is not accessible.");
    }

    public async Task<WorkDataImport?> GetImportAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId,
        WorkDataImportId id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString(); var key = "import:" + id;
        var payload = await db.WorkTrackingDocuments.AsNoTracking().Where(value => value.WorkspaceId == workspace
            && value.OwnerPrincipalId == owner && value.ProjectId == project && value.Key == key).Select(value => value.Payload).SingleOrDefaultAsync(token);
        return payload is null ? null : JsonSerializer.Deserialize<WorkDataImport>(payload, Json)
            ?? throw new InvalidOperationException("The persisted work import is invalid.");
    }

    public async Task<bool> SaveImportAsync(WorkDataImport operation, long expectedVersion, CancellationToken token)
    {
        if (operation.WorkspaceId.Value == Guid.Empty || operation.PrincipalId == Guid.Empty || operation.ProjectId.Value == Guid.Empty
            || operation.Id.Value == Guid.Empty || operation.Version != expectedVersion + 1 || operation.ProjectVersion < 1
            || operation.Selection.Plans.Count > WorkDataFormat.MaximumPlans || operation.Tasks.Count > 100)
            throw new ArgumentException("A bounded owned work import is required.", nameof(operation));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var workspace = operation.WorkspaceId.ToString(); var owner = operation.PrincipalId.ToString("D");
            var project = operation.ProjectId.ToString(); var key = "import:" + operation.Id;
            var row = await db.WorkTrackingDocuments.SingleOrDefaultAsync(value => value.WorkspaceId == workspace
                && value.OwnerPrincipalId == owner && value.ProjectId == project && value.Key == key, token);
            if (row is null ? expectedVersion != 0 : row.Version != expectedVersion) return false;
            if (expectedVersion == 0)
            {
                if (await db.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                    && value.Id == project && value.Version == operation.ProjectVersion)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return false;
                foreach (var plan in operation.Selection.Plans)
                {
                    var id = plan.Plan.PlanId.ToString();
                    if (plan.LocalVersion == 0)
                    {
                        if (await db.WorkPlans.AnyAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == id, token)) return false;
                    }
                    else if (await db.WorkPlans.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                        && value.Id == id && value.Version == plan.LocalVersion)
                        .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return false;
                }
                foreach (var task in operation.Tasks.Where(value => value.Version > 0))
                {
                    var id = task.Id.ToString();
                    if (await db.WorkItems.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                        && value.Id == id && value.Version == task.Version)
                        .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return false;
                }
            }
            var payload = JsonSerializer.Serialize(operation, Json);
            if (row is null) db.WorkTrackingDocuments.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner,
                ProjectId = project, Key = key, Version = operation.Version, Payload = payload });
            else { row.Version = operation.Version; row.Payload = payload; }
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return false; }
        });
    }
}
