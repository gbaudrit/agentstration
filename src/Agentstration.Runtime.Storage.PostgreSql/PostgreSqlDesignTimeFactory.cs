using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Runtime.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<RuntimeRunDbContext>
{
    public RuntimeRunDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<RuntimeRunDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "runtime"))
            .Options);
}
