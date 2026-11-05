using Agentstration.Infrastructure.Triggers;
using Agentstration.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Agentstration.Infrastructure;

internal static class TriggerSchedulingServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationTriggerScheduling(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var storageProvider = context.StorageProvider;
        var schedulerConnectionString = context.SchedulerConnectionString;
        var enableHostedServices = context.Options.EnableHostedServices;
        services.AddSingleton<ITriggerScheduleCalculator, QuartzTriggerScheduleCalculator>();
        services.AddSingleton<ITriggerOperationalContext, TriggerOperationalContextAdapter>();
        services.AddSingleton<ITriggerTargetValidator, FlowTriggerTargetValidator>();
        services.AddSingleton<ITriggerExecutionAuthorizer, WorkspaceTriggerExecutionAuthorizer>();
        services.AddSingleton<ITriggerWorkSubmitter, TriggerWorkSubmitter>();
        services.AddSingleton<ITriggerSchedulerProjection, QuartzTriggerScheduler>();
        services.AddSingleton<TriggerManagementService>();
        services.AddSingleton<TriggerFiringService>();
        // Quartz owns a short-lived, local scheduler database. Disabling ADO.NET pooling
        // ensures its file handles are released when the hosted scheduler shuts down.
        services.AddQuartz(configuration =>
        {
            configuration.SchedulerId = "AUTO";
            configuration.SchedulerName = "Agentstration.TriggerScheduler";
            configuration.UsePersistentStore(options =>
            {
                options.UseProperties = true;
                if (storageProvider == AgentstrationStorageProvider.PostgreSql)
                {
                    options.UsePostgres(postgres =>
                    {
                        postgres.ConnectionString = schedulerConnectionString;
                        postgres.TablePrefix = "scheduler.qrtz_";
                    });
                }
                else
                    options.UseMicrosoftSQLite(sqlite => sqlite.ConnectionString = schedulerConnectionString);
                options.UseSystemTextJsonSerializer();
            });
        });
        if (enableHostedServices)
        {
            services.AddSingleton<IHostedService, QuartzLoggingInitializer>();
            if (storageProvider == AgentstrationStorageProvider.Sqlite)
                services.AddSingleton<IHostedService>(_ => new QuartzSqliteSchemaInitializer(schedulerConnectionString));
            services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
            services.AddHostedService<TriggerSchedulerReconciler>();
        }
        return services;
    }
}
