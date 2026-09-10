using System.Threading.RateLimiting;
using Agentstration.Aep.Abstractions;
using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Flows;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Security.AspNetCoreIdentity.PostgreSql;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentstration.Web.Configuration;

internal sealed record WebHostServiceRegistrationOptions(
    LocalBootstrapOptions BootstrapOptions,
    ToolExecutionCaptureOptions ToolExecutionCaptureOptions,
    AgentstrationServiceRegistrationOptions PlatformOptions,
    AgentstrationStorageProvider StorageProvider,
    string IdentityConnectionString,
    string DataProtectionKeysPath,
    string? TestingStorageDirectory,
    IReadOnlyList<string> TestingSqliteConnectionStrings,
    bool UseManagedProfileResolver);

internal static class WebHostServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationWebHost(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        WebHostServiceRegistrationOptions options)
    {
        services.AddSingleton(options.BootstrapOptions);
        services.AddSingleton(options.ToolExecutionCaptureOptions);
        services.AddAgentstration(options.PlatformOptions);
        services.AddAgentstrationModelProviders(configuration, options.UseManagedProfileResolver);
        services.AddSingleton(configuration
            .GetSection(AepEnrollmentPolicyOptions.SectionName)
            .Get<AepEnrollmentPolicyOptions>() ?? new());
        services.AddAgentstrationModelManagement();
        services.AddAgentstrationExtensionDiscovery();
        services.AddAgentstrationServerTransport(configuration);
        services.AddAgentstrationIdentity(
            options.StorageProvider,
            options.IdentityConnectionString,
            options.DataProtectionKeysPath,
            environment);
        services.AddAgentstrationBootstrapHosting();
        services.AddAgentstrationRealtimeEvents();
        services.AddAgentstrationWebConsole(configuration, environment);
        services.AddAgentstrationBackgroundWorkers(options.PlatformOptions.EnableHostedServices);
        services.AddAgentstrationTestingCleanup(
            options.TestingStorageDirectory,
            options.TestingSqliteConnectionStrings);
        return services;
    }

    internal static WebApplicationBuilder AddAgentstrationObservability(
        this WebApplicationBuilder builder,
        bool enabled)
    {
        if (!enabled)
            return builder;

        var otlpEnabled = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(
                ResourceBuilder.CreateDefault().AddService("Agentstration.Web"));
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            if (otlpEnabled)
                logging.AddOtlpExporter();
        });
        builder.Services.AddOpenTelemetry()
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
                if (otlpEnabled)
                    tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(
                        WorkItemService.Meter.Name,
                        FlowRunService.Meter.Name,
                        AgentFrameworkRuntimeFactory.TelemetrySourceName,
                        GenAiObservabilityOptions.ChatClientSourceName);
                if (otlpEnabled)
                    metrics.AddOtlpExporter();
            });
        return builder;
    }

    private static IServiceCollection AddAgentstrationExtensionDiscovery(
        this IServiceCollection services)
    {
        services.AddSingleton<ExtensionSourceDiscoveryService>();
        services.AddSingleton<IAepEnrollmentAnnouncementProvisioner>(provider =>
            provider.GetRequiredService<ExtensionSourceDiscoveryService>());
        services.AddSingleton<StandardRuntimeProfileSeeder>();
        return services;
    }

    private static IServiceCollection AddAgentstrationServerTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = static async (context, token) =>
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        error = new AepEnrollmentError(
                            "rate_limited",
                            "Too many enrollment requests; retry later.")
                    },
                    token);
            options.AddPolicy("aep-enrollment-public", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
        services.AddAgentstrationOpenApi();
        services.AddRazorPages();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddAgentstrationLocalization(configuration);
        services.AddSignalR();
        services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
        return services;
    }

    private static IServiceCollection AddAgentstrationIdentity(
        this IServiceCollection services,
        AgentstrationStorageProvider storageProvider,
        string connectionString,
        string dataProtectionKeysPath,
        IHostEnvironment environment)
    {
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddAgentstrationPostgreSqlIdentity(
                connectionString,
                dataProtectionKeysPath,
                useDevelopmentPasswordPolicy: environment.IsDevelopment());
        }
        else
        {
            services.AddAgentstrationLocalIdentity(
                connectionString,
                dataProtectionKeysPath,
                useDevelopmentPasswordPolicy: environment.IsDevelopment());
        }
        return services;
    }

    private static IServiceCollection AddAgentstrationBootstrapHosting(
        this IServiceCollection services)
    {
        services.AddScoped<DeclarativeBootstrapService>();
        services.AddSingleton<BootstrapProfileCatalog>();
        services.AddSingleton<SourceBootstrapProfileLoader>();
        services.AddSingleton<BootstrapApplicationLock>();
        services.AddScoped<BootstrapProfileManagementService>();
        return services;
    }

    private static IServiceCollection AddAgentstrationRealtimeEvents(
        this IServiceCollection services)
    {
        services.AddSingleton<SignalRFlowRunEventSink>();
        services.AddSingleton<WorkplaceFlowConversationProjectionSink>();
        services.Replace(ServiceDescriptor.Singleton<IFlowRunEventSink>(provider =>
            new CompositeFlowRunEventSink(
            [
                provider.GetRequiredService<WorkplaceFlowConversationProjectionSink>(),
                provider.GetRequiredService<SignalRFlowRunEventSink>()
            ])));
        services.AddSingleton<IWorkplaceEventSink, SignalRWorkplaceEventSink>();
        return services;
    }

    private static IServiceCollection AddAgentstrationBackgroundWorkers(
        this IServiceCollection services,
        bool enabled)
    {
        if (!enabled)
            return services;

        services.AddHostedService<AgentDeploymentReconciliationWorker>();
        services.AddHostedService<LocalWorkExecutionWorker>();
        services.AddHostedService<RuntimeRunExecutionWorker>();
        services.AddHostedService<FlowRunExecutionWorker>();
        services.AddHostedService<FlowRunRecoveryWorker>();
        return services;
    }

    private static IServiceCollection AddAgentstrationTestingCleanup(
        this IServiceCollection services,
        string? testingStorageDirectory,
        IReadOnlyList<string> sqliteConnectionStrings)
    {
        if (testingStorageDirectory is null)
            return services;

        services.AddSingleton(provider => new TestingDataDirectoryCleanup(
            testingStorageDirectory,
            sqliteConnectionStrings,
            provider.GetRequiredService<ILogger<TestingDataDirectoryCleanup>>()));
        return services;
    }
}
