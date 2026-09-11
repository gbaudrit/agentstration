using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.ResourceManagement.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<ResourceManagementDbContext>
{
    public ResourceManagementDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ResourceManagementDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "management"))
            .Options);
}
