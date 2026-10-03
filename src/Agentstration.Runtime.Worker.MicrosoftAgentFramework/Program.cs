using Agentstration.Runtime.Worker.MicrosoftAgentFramework;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["OTEL_SERVICE_NAME"]
    ?? "Agentstration.Runtime.Worker.MicrosoftAgentFramework";
var configuredWorkerId = builder.Configuration.GetValue<Guid?>(
    $"{RuntimeWorkerOptions.SectionName}:WorkerId");
var resource = ResourceBuilder.CreateDefault().AddService(
    serviceName,
    serviceInstanceId: configuredWorkerId is { } workerId && workerId != Guid.Empty
        ? workerId.ToString("D")
        : null);
var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resource);
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
    if (otlpEnabled) logging.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(configure => configure.AddService(
        serviceName,
        serviceInstanceId: configuredWorkerId is { } workerId && workerId != Guid.Empty
            ? workerId.ToString("D")
            : null))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(
                AwpRuntimeWorkerService.ActivitySource.Name,
                Agentstration.Runtime.MicrosoftAgentFramework.AgentFrameworkRuntimeFactory.TelemetrySourceName,
                Agentstration.ModelProviders.GenAiObservabilityOptions.ChatClientSourceName);
        if (otlpEnabled) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(
                Agentstration.Runtime.MicrosoftAgentFramework.AgentFrameworkRuntimeFactory.TelemetrySourceName,
                Agentstration.ModelProviders.GenAiObservabilityOptions.ChatClientSourceName);
        if (otlpEnabled) metrics.AddOtlpExporter();
    });
builder.Services.AddOptions<RuntimeWorkerOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeWorkerOptions.SectionName))
    .Validate(options => options.Validate(), "The Runtime Worker configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeWorkerReadiness>();
builder.Services.AddSingleton<AwpWorkerCredentialProvider>();
builder.Services.AddSingleton<AwpAssignmentExecutor>();
builder.Services.AddHostedService<AwpRuntimeWorkerService>();
builder.Services.AddHealthChecks()
    .AddCheck<RuntimeWorkerLivenessCheck>("runtime-worker-live")
    .AddCheck<RuntimeWorkerReadinessCheck>("runtime-worker-ready", tags: ["ready"]);

var app = builder.Build();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready", StringComparer.Ordinal)
});
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
