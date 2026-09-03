using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Management.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "management"))
            .Options);
}
