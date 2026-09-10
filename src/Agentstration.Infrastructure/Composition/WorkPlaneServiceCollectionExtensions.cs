using Agentstration.Application.Work;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Work;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.PostgreSql;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class WorkPlaneServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationWorkPlane(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        if (context.StorageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddPostgreSqlWorkPlane(context.StorageOptions.ConnectionString!);
        }
        else
        {
            services.AddSqliteWorkPlane(
                context.WorkPlaneConnectionString
                ?? $"Data Source={Path.Combine(context.DataDirectory, "work-plane.db")}");
        }

        services.AddSingleton<IArtifactStore>(_ =>
            new FileSystemArtifactStore(Path.Combine(context.DataDirectory, "artifacts")));
        services.AddSingleton<LocalWorkExecutionGateway>();
        services.AddSingleton<IWorkExecutionGateway>(provider =>
            provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<ILocalWorkExecutionQueue>(provider =>
            provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<WorkItemService>();
        services.AddSingleton<WorkplaceService>();
        services.AddSingleton<WorkTaskDeletionService>();
        services.AddSingleton<IWorkTaskEventSink, WorkplaceProjectionSink>();
        return services;
    }
}
