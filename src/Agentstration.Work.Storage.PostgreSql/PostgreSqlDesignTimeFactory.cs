using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<WorkDbContext>
{
    public WorkDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WorkDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "work"))
            .Options);
}
