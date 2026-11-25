using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class SqliteWorkDataRepository(IDbContextFactory<WorkDbContext> factory) : IWorkDataRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.EnsureCreatedAsync(token);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorkTrackingDocuments (WorkspaceId TEXT NOT NULL, OwnerPrincipalId TEXT NOT NULL,
                ProjectId TEXT NOT NULL, Key TEXT NOT NULL, Version INTEGER NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, OwnerPrincipalId, ProjectId, Key));
            """, token);
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
                foreach (var task in operation.Tasks.Where(value => value.Version > 0))
                {
                    var id = task.Id.ToString();
                    if (await db.WorkItems.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                        && value.Id == id && value.Version == task.Version)
                        .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return false;
                }
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
                if (await db.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                    && value.Id == project && value.Version == operation.ProjectVersion)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.Version, value => value.Version), token) != 1) return false;
                if (operation.Selection.Organization is not null)
                {
                    var organization = await db.WorkTrackingDocuments.AsNoTracking().Where(value => value.WorkspaceId == workspace
                        && value.OwnerPrincipalId == owner && value.ProjectId == project && value.Key == "organization").Select(value => value.Payload).SingleOrDefaultAsync(token);
                    var version = organization is null ? 0 : JsonSerializer.Deserialize<WorkOrganizationSnapshot>(organization, Json)!.Version;
                    if (version != operation.Selection.OrganizationVersion) return false;
                }
            }
            var payload = JsonSerializer.Serialize(operation, Json);
            if (row is null) db.WorkTrackingDocuments.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner,
                ProjectId = project, Key = key, Version = operation.Version, Payload = payload });
            else { row.Version = operation.Version; row.Payload = payload; }
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 }) { return false; }
        });
    }
    public async Task<WorkOrganizationSnapshot?> GetOrganizationAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectId projectId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = principalId.ToString("D"); var project = projectId.ToString();
        var payload = await db.WorkTrackingDocuments.AsNoTracking().Where(value => value.WorkspaceId == workspace
            && value.OwnerPrincipalId == owner && value.ProjectId == project && value.Key == "organization")
            .Select(value => value.Payload).SingleOrDefaultAsync(token);
        return payload is null ? null : JsonSerializer.Deserialize<WorkOrganizationSnapshot>(payload, Json)
            ?? throw new InvalidOperationException("The persisted Work organization is invalid.");
    }

    public async Task<bool> SaveOrganizationAsync(WorkOrganizationSnapshot value, long expectedStorageVersion, long expectedProjectVersion, CancellationToken token)
    {
        if (value.WorkspaceId.Value == Guid.Empty || value.PrincipalId == Guid.Empty || value.ProjectId.Value == Guid.Empty
            || value.StorageVersion != expectedStorageVersion + 1 || value.Version < 0 || value.Commands.Count > 256
            || !value.Definition.Validate().IsSuccess)
            throw new ArgumentException("A bounded owned organization change is required.", nameof(value));
        await using var strategy = await factory.CreateDbContextAsync(token);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var workspace = value.WorkspaceId.ToString(); var owner = value.PrincipalId.ToString("D"); var project = value.ProjectId.ToString();
            if (await db.WorkProjects.Where(row => row.WorkspaceId == workspace && row.OwnerPrincipalId == owner
                && row.Id == project && row.Version == expectedProjectVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Version, row => row.Version), token) != 1) return false;
            var row = await db.WorkTrackingDocuments.SingleOrDefaultAsync(row => row.WorkspaceId == workspace
                && row.OwnerPrincipalId == owner && row.ProjectId == project && row.Key == "organization", token);
            if (row is null ? expectedStorageVersion != 0 : row.Version != expectedStorageVersion) return false;
            var payload = JsonSerializer.Serialize(value, Json);
            if (row is null) db.WorkTrackingDocuments.Add(new() { WorkspaceId = workspace, OwnerPrincipalId = owner,
                ProjectId = project, Key = "organization", Version = value.StorageVersion, Payload = payload });
            else { row.Version = value.StorageVersion; row.Payload = payload; }
            try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 }) { return false; }
        });
    }

}
