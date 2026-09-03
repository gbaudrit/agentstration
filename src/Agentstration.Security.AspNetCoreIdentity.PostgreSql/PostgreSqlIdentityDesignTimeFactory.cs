using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentstration.Security.AspNetCoreIdentity.PostgreSql;

public sealed class PostgreSqlIdentityDesignTimeFactory : IDesignTimeDbContextFactory<LocalIdentityDbContext>
{
    public LocalIdentityDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<LocalIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstration;Username=postgres;Password=postgres", options => options
                .MigrationsAssembly(typeof(PostgreSqlIdentityDesignTimeFactory).Assembly.FullName)
                .MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options);
}
