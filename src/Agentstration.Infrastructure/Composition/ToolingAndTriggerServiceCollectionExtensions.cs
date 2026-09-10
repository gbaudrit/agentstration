using Agentstration.Infrastructure.Triggers;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Agentstration.Infrastructure;

internal static class ToolingAndTriggerServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationToolingAndTriggers(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        services.AddSingleton<ToolManagementService>();
        services.AddSingleton<ToolExecutionHookManagementService>();
        services.AddSingleton<RuntimeProfileManagementService>();
        services.AddSingleton<ITriggerScheduleCalculator, QuartzTriggerScheduleCalculator>();
        services.AddSingleton<ITriggerTargetValidator, FlowTriggerTargetValidator>();
        services.AddSingleton<ITriggerExecutionAuthorizer, WorkspaceTriggerExecutionAuthorizer>();
        services.AddSingleton<ITriggerWorkSubmitter, TriggerWorkSubmitter>();
        services.AddSingleton<ITriggerSchedulerProjection, QuartzTriggerScheduler>();
        services.AddSingleton<TriggerManagementService>();
        services.AddSingleton<TriggerFiringService>();

        services.AddQuartz(configuration =>
        {
            configuration.SchedulerId = "AUTO";
            configuration.SchedulerName = "Agentstration.TriggerScheduler";
            configuration.UsePersistentStore(options =>
            {
                options.UseProperties = true;
                if (context.StorageProvider == AgentstrationStorageProvider.PostgreSql)
                {
                    options.UsePostgres(postgres =>
                    {
                        postgres.ConnectionString = context.SchedulerConnectionString;
                        postgres.TablePrefix = "scheduler.qrtz_";
                    });
                }
                else
                {
                    // Quartz owns a short-lived local database. Pooling is disabled so
                    // shutdown releases the scheduler file handles deterministically.
                    options.UseMicrosoftSQLite(sqlite =>
                        sqlite.ConnectionString = context.SchedulerConnectionString);
                }
                options.UseSystemTextJsonSerializer();
            });
        });

        if (context.EnableHostedServices)
        {
            services.AddSingleton<IHostedService, QuartzLoggingInitializer>();
            if (context.StorageProvider == AgentstrationStorageProvider.Sqlite)
            {
                services.AddSingleton<IHostedService>(_ =>
                    new QuartzSqliteSchemaInitializer(context.SchedulerConnectionString));
            }
            services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
            services.AddHostedService<TriggerSchedulerReconciler>();
        }

        return services;
    }
}
