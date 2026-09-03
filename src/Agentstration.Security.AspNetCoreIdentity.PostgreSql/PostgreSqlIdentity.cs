using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Security.AspNetCoreIdentity.PostgreSql;

public static class PostgreSqlIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationPostgreSqlIdentity(
        this IServiceCollection services,
        string connectionString,
        string dataProtectionKeysPath,
        bool useDevelopmentPasswordPolicy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddAgentstrationIdentity(
            options => options.UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(typeof(PostgreSqlIdentityServiceCollectionExtensions).Assembly.FullName)
                .MigrationsHistoryTable("__EFMigrationsHistory", "identity")
                .EnableRetryOnFailure()).AddInterceptors(new UtcDateTimeOffsetInterceptor()),
            dataProtectionKeysPath,
            useDevelopmentPasswordPolicy);
    }
}
