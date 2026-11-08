using Agentstration.Flows.Storage.PostgreSql;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.ResourceManagement.Storage.PostgreSql;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.ResourcePlanning.Storage.PostgreSql;
using Agentstration.ResourcePlanning.Storage.Sqlite;
using Agentstration.Runtime.Storage.PostgreSql;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Work.Storage.PostgreSql;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class StorageServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationStorage(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var storageOptions = context.Options.Storage;
        var storageProvider = context.StorageProvider;
        services.AddSingleton(storageOptions);
        services.AddSingleton<InstanceInitializationOptions>();
        services.AddSingleton<IInstanceInitializationCoordinator, InstanceInitializationCoordinator>();
        services.AddSingleton<WorkspacePlatformResourceProvisioner>();
        services.AddSingleton<ConnectedServicePlatformResourceProvisioner>();
        services.AddStorageResources();
        services.AddSingleton<IWorkspacePlatformResourceProvisioner>(provider => provider.GetRequiredService<WorkspacePlatformResourceProvisioner>());
        services.AddSingleton<IWorkspaceProvisioner>(provider => provider.GetRequiredService<WorkspacePlatformResourceProvisioner>());
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddSingleton<IAgentstrationStorageInitializer, PostgreSqlStorageInitializer>();
        else
            services.AddSingleton<IAgentstrationStorageInitializer, SqliteStorageInitializer>();
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddPostgreSqlResourceManagement(storageOptions.ConnectionString!);
            services.AddPostgreSqlResourcePlanning(storageOptions.ConnectionString!);
            services.AddPostgreSqlRuntimeRuns(storageOptions.ConnectionString!);
            services.AddPostgreSqlWorkPlane(storageOptions.ConnectionString!);
            services.AddPostgreSqlFlowStorage(storageOptions.ConnectionString!);
        }
        else
        {
            services.AddSqliteResourceManagement(context.ControlPlaneConnectionString);
            services.AddSqliteResourcePlanning(context.ResourcePlanningConnectionString);
            services.AddSqliteRuntimeRuns(context.RuntimeConnectionString);
            services.AddSqliteWorkPlane(context.WorkPlaneConnectionString);
            services.AddSqliteFlowStorage(context.FlowConnectionString);
        }
        return services;
    }
}
