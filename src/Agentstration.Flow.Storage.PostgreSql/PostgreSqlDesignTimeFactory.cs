using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Flow.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<FlowDbContext>
{
    public FlowDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<FlowDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "flow"))
            .Options);
}
