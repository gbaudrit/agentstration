using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<ResourcePlanningDbContext>
{
    public ResourcePlanningDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ResourcePlanningDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres")
            .Options);
}
