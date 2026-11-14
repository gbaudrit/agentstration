using Agentstration.Http.Diagnostics;
using Agentstration.Web.Components.Localization;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentstration.Console.Web.Configuration;

internal static class ConsolePresentationServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationConsolePresentation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddRazorPages();
        services.AddAgentstrationLocalization(configuration);
        services.AddProblemDetails();
        services.AddHealthChecks();
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleObservability(
        this IServiceCollection services,
        ILoggingBuilder loggingBuilder,
        IConfiguration configuration)
    {
        var otlpEnabled = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        loggingBuilder.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Agentstration.Console.Web"));
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            if (otlpEnabled) logging.AddOtlpExporter();
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Agentstration.Console.Web"))
            .WithTracing(tracing =>
            {
                tracing.AddSource(HttpDiagnosticsExtensions.ActivitySourceName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (otlpEnabled) tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (otlpEnabled) metrics.AddOtlpExporter();
            });
        return services;
    }
}
