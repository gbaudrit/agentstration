using Agentstration.Infrastructure.Runtime;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Storage.PostgreSql;
using Agentstration.Runtime.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class RuntimeRunServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationRuntimeRuns(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        if (context.StorageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddPostgreSqlRuntimeRuns(context.StorageOptions.ConnectionString!);
        }
        else
        {
            services.AddSqliteRuntimeRuns(
                context.RuntimeConnectionString
                ?? $"Data Source={Path.Combine(context.DataDirectory, "runtime-plane.db")}");
        }

        services.AddSingleton<RuntimeRunStateManager>();
        services.AddSingleton<RuntimeRunService>();
        services.TryAddSingleton(new ToolExecutionCaptureOptions());
        services.AddSingleton<IToolExecutionEventSink, RuntimeToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionEventSink, FlowToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionHookResolver, ManagementToolExecutionHookResolver>();
        services.AddSingleton<IToolGovernanceAuditReader, ToolGovernanceAuditReader>();
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        return services;
    }
}
