using Agentstration.Web.Components;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Components.State;
using Agentstration.Workplace.Client;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agentstration.Workplace.Web;

internal static class WorkplaceWebHostServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationWorkplaceHost(
        this IServiceCollection services,
        IConfiguration configuration,
        Uri apiUrl,
        Uri hubUrl)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddAgentstrationWebComponents();
        services.AddScoped<IRecentConversationNavigationProvider, WorkplaceRecentConversationNavigationProvider>();
        services.AddAgentstrationLocalization(configuration);
        services.AddHttpContextAccessor();
        services.AddTransient(provider => new WorkplaceApiSessionHandler(
            provider.GetRequiredService<IHttpContextAccessor>(),
            apiUrl,
            ".Agentstration.Identity.Application",
            "agentstration.workspace"));
        services.AddScoped<IWorkplaceRealtimeConnectionOptionsConfigurator>(provider =>
            new WorkplaceRealtimeSession(
                provider.GetRequiredService<IHttpContextAccessor>(),
                hubUrl,
                ".Agentstration.Identity.Application",
                "agentstration.workspace"));
        services.AddAgentstrationWorkplaceClient(apiUrl, hubUrl)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<WorkplaceApiSessionHandler>();
        services.AddHttpClient<IUserPreferencesClient, HttpUserPreferencesClient>(client =>
                client.BaseAddress = apiUrl)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<WorkplaceApiSessionHandler>();
        services.AddProblemDetails();
        services.AddHealthChecks();
        return services;
    }

    internal static WebApplicationBuilder AddAgentstrationWorkplaceObservability(
        this WebApplicationBuilder builder)
    {
        var otlp = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(value => value.AddService("Agentstration.Workplace.Web"))
            .WithTracing(value =>
            {
                value.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (otlp)
                    value.AddOtlpExporter();
            })
            .WithMetrics(value =>
            {
                value.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (otlp)
                    value.AddOtlpExporter();
            });
        return builder;
    }
}
