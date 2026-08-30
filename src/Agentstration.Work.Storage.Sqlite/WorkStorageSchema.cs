using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Work.Storage.Sqlite;

internal static class WorkDashboardSchema
{
    public static async Task EnsureAsync(WorkDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS WorkplaceDashboards (Id TEXT NOT NULL CONSTRAINT PK_WorkplaceDashboards PRIMARY KEY, WorkspaceId TEXT NOT NULL, Name TEXT NOT NULL, Payload TEXT NOT NULL);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkplaceDashboards_WorkspaceId_Name ON WorkplaceDashboards (WorkspaceId, Name);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS WorkplaceDashboardDrafts (Id TEXT NOT NULL CONSTRAINT PK_WorkplaceDashboardDrafts PRIMARY KEY, WorkspaceId TEXT NOT NULL, Name TEXT NOT NULL, Payload TEXT NOT NULL);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkplaceDashboardDrafts_WorkspaceId_Name ON WorkplaceDashboardDrafts (WorkspaceId, Name);",
            cancellationToken);
    }
}

internal static class WorkEntrySchema
{
    public static async Task EnsureAsync(WorkDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Entries_WorkspaceId_Name ON Entries (WorkspaceId, Name);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_EntryDrafts_WorkspaceId_Name ON EntryDrafts (WorkspaceId, Name);", cancellationToken);
    }
}

public static class SqliteWorkServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteWorkPlane(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<WorkDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IWorkItemRepository, SqliteWorkItemRepository>();
        services.AddSingleton<IWorkplaceRepository, SqliteWorkplaceRepository>();
        return services;
    }
}

