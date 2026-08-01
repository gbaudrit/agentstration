using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Infrastructure.Persistence.Postgres;

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__agentstration") ?? "Host=localhost;Port=5432;Database=agentstration;Username=postgres;Password=postgres";
        return new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
    }
}
