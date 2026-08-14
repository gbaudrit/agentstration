using System.Data;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Management.Storage.Sqlite;

internal sealed class TenantRow { public Guid Id { get; set; } public required string Name { get; set; } public required string DisplayName { get; set; } public TenantStatus Status { get; set; } public DateTimeOffset CreatedAt { get; set; } }
internal sealed class WorkspaceRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public required string Name { get; set; } public required string DisplayName { get; set; } public WorkspaceStatus Status { get; set; } public DateTimeOffset CreatedAt { get; set; } }
internal sealed class UserRow { public Guid Id { get; set; } public string? ExternalSubject { get; set; } public required string DisplayName { get; set; } public string? Email { get; set; } public UserStatus Status { get; set; } public DateTimeOffset CreatedAt { get; set; } }
internal sealed class TenantMembershipRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid UserId { get; set; } public MembershipStatus Status { get; set; } public DateTimeOffset JoinedAt { get; set; } }
internal sealed class RoleDefinitionRow { public Guid Id { get; set; } public required string Name { get; set; } public required string DisplayName { get; set; } public required string PermissionsJson { get; set; } public bool IsBuiltIn { get; set; } }
internal sealed class RoleAssignmentRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid PrincipalId { get; set; } public PrincipalType PrincipalType { get; set; } public Guid RoleDefinitionId { get; set; } public required string Scope { get; set; } }

internal static class IdentityModel
{
    public static void ConfigureIdentityModel(this ModelBuilder modelBuilder)
    {
        var tenant = modelBuilder.Entity<TenantRow>(); tenant.ToTable("Tenants"); tenant.HasKey(x => x.Id); tenant.HasIndex(x => x.Name).IsUnique();
        var workspace = modelBuilder.Entity<WorkspaceRow>(); workspace.ToTable("Workspaces"); workspace.HasKey(x => x.Id); workspace.HasIndex(x => new { x.TenantId, x.Name }).IsUnique(); workspace.HasOne<TenantRow>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        var user = modelBuilder.Entity<UserRow>(); user.ToTable("Users"); user.HasKey(x => x.Id); user.HasIndex(x => x.ExternalSubject).IsUnique();
        var membership = modelBuilder.Entity<TenantMembershipRow>(); membership.ToTable("TenantMemberships"); membership.HasKey(x => x.Id); membership.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique(); membership.HasOne<TenantRow>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade); membership.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        var role = modelBuilder.Entity<RoleDefinitionRow>(); role.ToTable("RoleDefinitions"); role.HasKey(x => x.Id); role.HasIndex(x => x.Name).IsUnique();
        var assignment = modelBuilder.Entity<RoleAssignmentRow>(); assignment.ToTable("RoleAssignments"); assignment.HasKey(x => x.Id); assignment.HasIndex(x => new { x.TenantId, x.PrincipalId }); assignment.HasIndex(x => new { x.TenantId, x.Scope }); assignment.HasIndex(x => new { x.TenantId, x.PrincipalId, x.RoleDefinitionId, x.Scope }).IsUnique();
    }
}

internal static class SqliteIdentitySchema
{
    public static async Task EnsureAsync(ControlPlaneDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await AddColumnIfMissingAsync(connection, "ControlPlaneResources", "TenantId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ControlPlaneResources", "WorkspaceId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ControlPlaneResources", "Uid", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ControlPlaneResources", "Kind", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ControlPlaneResources", "Name", "TEXT NULL", cancellationToken);
        string[] commands =
        [
            "CREATE TABLE IF NOT EXISTS Tenants (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, DisplayName TEXT NOT NULL, Status INTEGER NOT NULL, CreatedAt TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_Tenants_Name ON Tenants(Name);",
            "CREATE TABLE IF NOT EXISTS Workspaces (Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, Name TEXT NOT NULL, DisplayName TEXT NOT NULL, Status INTEGER NOT NULL, CreatedAt TEXT NOT NULL, FOREIGN KEY(TenantId) REFERENCES Tenants(Id) ON DELETE CASCADE); CREATE UNIQUE INDEX IF NOT EXISTS IX_Workspaces_TenantId_Name ON Workspaces(TenantId, Name);",
            "CREATE TABLE IF NOT EXISTS Users (Id TEXT NOT NULL PRIMARY KEY, ExternalSubject TEXT NULL, DisplayName TEXT NOT NULL, Email TEXT NULL, Status INTEGER NOT NULL, CreatedAt TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_ExternalSubject ON Users(ExternalSubject);",
            "CREATE TABLE IF NOT EXISTS TenantMemberships (Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, UserId TEXT NOT NULL, Status INTEGER NOT NULL, JoinedAt TEXT NOT NULL, FOREIGN KEY(TenantId) REFERENCES Tenants(Id) ON DELETE CASCADE, FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE); CREATE UNIQUE INDEX IF NOT EXISTS IX_TenantMemberships_TenantId_UserId ON TenantMemberships(TenantId, UserId);",
            "CREATE TABLE IF NOT EXISTS RoleDefinitions (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, DisplayName TEXT NOT NULL, PermissionsJson TEXT NOT NULL, IsBuiltIn INTEGER NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_RoleDefinitions_Name ON RoleDefinitions(Name);",
            "CREATE TABLE IF NOT EXISTS RoleAssignments (Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, PrincipalId TEXT NOT NULL, PrincipalType INTEGER NOT NULL, RoleDefinitionId TEXT NOT NULL, Scope TEXT NOT NULL); CREATE INDEX IF NOT EXISTS IX_RoleAssignments_TenantId_PrincipalId ON RoleAssignments(TenantId, PrincipalId); CREATE INDEX IF NOT EXISTS IX_RoleAssignments_TenantId_Scope ON RoleAssignments(TenantId, Scope); CREATE UNIQUE INDEX IF NOT EXISTS IX_RoleAssignments_Unique ON RoleAssignments(TenantId, PrincipalId, RoleDefinitionId, Scope);",
            "DROP INDEX IF EXISTS IX_ControlPlaneResources_LogicalIdentity; CREATE UNIQUE INDEX IF NOT EXISTS IX_ControlPlaneResources_LogicalIdentity ON ControlPlaneResources(WorkspaceId, Namespace, Kind, Name);"
        ];
        foreach (var sql in commands) await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(System.Data.Common.DbConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SqliteIdentityStore(IDbContextFactory<ControlPlaneDbContext> contextFactory) : IIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<Tenant?> FindTenantByNameAsync(string name, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, token)); }
    public async Task<Tenant?> GetTenantAsync(Guid id, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)); }
    public async Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken token) { await using var db = await CreateAsync(token); return (await db.Tenants.AsNoTracking().OrderBy(x => x.Name).ToArrayAsync(token)).Select(x => Map(x)!).ToArray(); }
    public Task AddTenantAsync(Tenant x, CancellationToken token) => AddAsync(db => db.Tenants.Add(new() { Id = x.Id, Name = x.Name, DisplayName = x.DisplayName, Status = x.Status, CreatedAt = x.CreatedAt }), token);
    public async Task<Workspace?> FindWorkspaceByNameAsync(Guid tenantId, string name, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Name == name, token)); }
    public async Task<Workspace?> GetWorkspaceAsync(Guid tenantId, Guid id, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, token)); }
    public async Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(Guid tenantId, CancellationToken token) { await using var db = await CreateAsync(token); return (await db.Workspaces.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Name).ToArrayAsync(token)).Select(x => Map(x)!).ToArray(); }
    public Task AddWorkspaceAsync(Workspace x, CancellationToken token) => AddAsync(db => db.Workspaces.Add(new() { Id = x.Id, TenantId = x.TenantId, Name = x.Name, DisplayName = x.DisplayName, Status = x.Status, CreatedAt = x.CreatedAt }), token);
    public async Task<User?> FindUserByExternalSubjectAsync(string subject, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.ExternalSubject == subject, token)); }
    public async Task<User?> GetUserAsync(Guid id, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)); }
    public Task AddUserAsync(User x, CancellationToken token) => AddAsync(db => db.Users.Add(new() { Id = x.Id, ExternalSubject = x.ExternalSubject, DisplayName = x.DisplayName, Email = x.Email, Status = x.Status, CreatedAt = x.CreatedAt }), token);
    public async Task<TenantMembership?> FindMembershipAsync(Guid tenantId, Guid userId, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, token)); }
    public async Task<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid tenantId, CancellationToken token) { await using var db = await CreateAsync(token); return (await db.TenantMemberships.AsNoTracking().Where(x => x.TenantId == tenantId).ToArrayAsync(token)).Select(x => Map(x)!).ToArray(); }
    public Task AddMembershipAsync(TenantMembership x, CancellationToken token) => AddAsync(db => db.TenantMemberships.Add(new() { Id = x.Id, TenantId = x.TenantId, UserId = x.UserId, Status = x.Status, JoinedAt = x.JoinedAt }), token);
    public async Task<RoleDefinition?> FindRoleDefinitionByNameAsync(string name, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.RoleDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, token)); }
    public async Task<RoleDefinition?> GetRoleDefinitionAsync(Guid id, CancellationToken token) { await using var db = await CreateAsync(token); return Map(await db.RoleDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)); }
    public Task AddRoleDefinitionAsync(RoleDefinition x, CancellationToken token) => AddAsync(db => db.RoleDefinitions.Add(new() { Id = x.Id, Name = x.Name, DisplayName = x.DisplayName, PermissionsJson = JsonSerializer.Serialize(x.Permissions, JsonOptions), IsBuiltIn = x.IsBuiltIn }), token);
    public async Task<IReadOnlyList<RoleAssignment>> ListRoleAssignmentsAsync(Guid tenantId, Guid principalId, CancellationToken token) { await using var db = await CreateAsync(token); return (await db.RoleAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.PrincipalId == principalId).ToArrayAsync(token)).Select(x => Map(x)!).ToArray(); }
    public Task AddRoleAssignmentAsync(RoleAssignment x, CancellationToken token) => AddAsync(db => db.RoleAssignments.Add(new() { Id = x.Id, TenantId = x.TenantId, PrincipalId = x.PrincipalId, PrincipalType = x.PrincipalType, RoleDefinitionId = x.RoleDefinitionId, Scope = x.Scope }), token);
    private async Task<ControlPlaneDbContext> CreateAsync(CancellationToken token) => await contextFactory.CreateDbContextAsync(token);
    private async Task AddAsync(Action<ControlPlaneDbContext> add, CancellationToken token) { await using var db = await CreateAsync(token); add(db); try { await db.SaveChangesAsync(token); } catch (DbUpdateException ex) { throw new ControlPlaneConcurrencyException(ex.InnerException?.Message ?? ex.Message); } }
    private static Tenant? Map(TenantRow? x) => x is null ? null : new(x.Id, x.Name, x.DisplayName, x.Status, x.CreatedAt);
    private static Workspace? Map(WorkspaceRow? x) => x is null ? null : new(x.Id, x.TenantId, x.Name, x.DisplayName, x.Status, x.CreatedAt);
    private static User? Map(UserRow? x) => x is null ? null : new(x.Id, x.ExternalSubject, x.DisplayName, x.Email, x.Status, x.CreatedAt);
    private static TenantMembership? Map(TenantMembershipRow? x) => x is null ? null : new(x.Id, x.TenantId, x.UserId, x.Status, x.JoinedAt);
    private static RoleDefinition? Map(RoleDefinitionRow? x) => x is null ? null : new(x.Id, x.Name, x.DisplayName, JsonSerializer.Deserialize<string[]>(x.PermissionsJson, JsonOptions) ?? [], x.IsBuiltIn);
    private static RoleAssignment? Map(RoleAssignmentRow? x) => x is null ? null : new(x.Id, x.TenantId, x.PrincipalId, x.PrincipalType, x.RoleDefinitionId, x.Scope);
}
