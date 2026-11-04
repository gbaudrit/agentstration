using Agentstration.Application.Work;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Flows.Application;
using Agentstration.Identity;
using Agentstration.Infrastructure;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.MicrosoftAgentFramework;
using Agentstration.Runtime.Profiles;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Security.AspNetCoreIdentity.PostgreSql;
using Agentstration.Sources;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Configuration;
using Agentstration.Work;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentstration.Web.Hosting;

internal static class StandaloneHostServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationBootstrapHosting(
        this IServiceCollection services,
        LocalBootstrapOptions bootstrapOptions,
        ToolExecutionCaptureOptions toolExecutionCapture)
    {
        services.AddSingleton(bootstrapOptions);
        services.AddSingleton(toolExecutionCapture);
        services.AddScoped<DeclarativeBootstrapService>();
        return services;
    }

    internal static IServiceCollection AddAgentstrationExtensionHosting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<SourceProviderManagementService>();
        services.AddSingleton<ExtensionRegistrationManagementService>();
        services.AddSingleton<AepEnrollmentSettingsService>();
        services.AddSingleton<AepEnrollmentService>();
        services.AddSingleton<AwpWorkerIdentityService>();
        services.AddSingleton<StandardRuntimeProfileSeeder>();
        services.AddSingleton<ResourceScopeInventoryService>();
        services.AddSingleton<ExtensionManagementService>();
        services.AddSingleton<ExtensionInventoryService>();
        services.AddSingleton(configuration
            .GetSection(AepEnrollmentPolicyOptions.SectionName)
            .Get<AepEnrollmentPolicyOptions>() ?? new());
        services.AddSingleton<ExtensionSourceDiscoveryService>();
        services.AddSingleton<IAepEnrollmentAnnouncementProvisioner>(provider => provider.GetRequiredService<ExtensionSourceDiscoveryService>());
        return services;
    }

    internal static IServiceCollection AddAgentstrationServerPresentation(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool apiOnlyTesting)
    {
        services.AddAgentstrationApi(configuration, environment);
        if (!apiOnlyTesting)
        {
            services.AddRazorPages();
            services.AddRazorComponents().AddInteractiveServerComponents();
        }
        services.AddAgentstrationLocalization(configuration);
        return services;
    }

    internal static IServiceCollection AddAgentstrationHostIdentity(
        this IServiceCollection services,
        AgentstrationStorageProvider storageProvider,
        string connectionString,
        string dataProtectionKeysPath,
        bool useDevelopmentPasswordPolicy)
    {
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddAgentstrationPostgreSqlIdentity(connectionString, dataProtectionKeysPath, useDevelopmentPasswordPolicy);
        else
            services.AddAgentstrationLocalIdentity(connectionString, dataProtectionKeysPath, useDevelopmentPasswordPolicy);
        return services;
    }

    internal static IServiceCollection AddAgentstrationBackgroundWorkers(this IServiceCollection services, bool enabled)
    {
        if (!enabled) return services;
        services.AddHostedService<LocalWorkExecutionWorker>();
        services.AddHostedService<RuntimeAssignmentLeaseReaper>();
        services.AddHostedService<FlowRunRecoveryWorker>();
        services.AddHostedService<SourceRefreshWorker>();
        return services;
    }

    internal static IServiceCollection AddAgentstrationTestingCleanup(
        this IServiceCollection services,
        string? testingStorageDirectory,
        string[] sqliteConnectionStrings)
    {
        if (testingStorageDirectory is null) return services;
        services.AddSingleton(provider => new TestingDataDirectoryCleanup(
            testingStorageDirectory,
            sqliteConnectionStrings,
            provider.GetRequiredService<ILogger<TestingDataDirectoryCleanup>>()));
        return services;
    }

    internal static IServiceCollection AddAgentstrationHostObservability(
        this IServiceCollection services,
        ILoggingBuilder loggingBuilder,
        IConfiguration configuration,
        bool enabled)
    {
        if (!enabled) return services;

        var otlpEnabled = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        loggingBuilder.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Agentstration.Web"));
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            if (otlpEnabled) logging.AddOtlpExporter();
        });
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Agentstration.Web"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(
                        WorkItemService.ActivitySource.Name,
                        RuntimeRunService.ActivitySource.Name,
                        FlowRunService.ActivitySource.Name,
                        AgentFrameworkRuntimeFactory.TelemetrySourceName,
                        GenAiObservabilityOptions.ChatClientSourceName,
                        GenAiHttpPayloadCaptureHandler.TelemetrySourceName);
                if (otlpEnabled) tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(
                        WorkItemService.Meter.Name,
                        FlowRunService.Meter.Name,
                        SourceRegistryManagementService.Meter.Name,
                        AgentFrameworkRuntimeFactory.TelemetrySourceName,
                        GenAiObservabilityOptions.ChatClientSourceName);
                if (otlpEnabled) metrics.AddOtlpExporter();
            });
        return services;
    }
}
