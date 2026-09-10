using Agentstration.Management.Abstractions;
using Agentstration.Management.Storage.PostgreSql;
using Agentstration.Management.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class ControlPlaneServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationControlPlane(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        services.AddSingleton(context.StorageOptions);
        if (context.StorageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddSingleton<IAgentstrationStorageInitializer, PostgreSqlStorageInitializer>();
            services.AddPostgreSqlControlPlane(context.StorageOptions.ConnectionString!);
        }
        else
        {
            services.AddSingleton<IAgentstrationStorageInitializer, SqliteStorageInitializer>();
            services.AddSqliteControlPlane(
                context.ControlPlaneConnectionString
                ?? $"Data Source={Path.Combine(context.DataDirectory, "control-plane.db")}");
        }

        return services;
    }
}
