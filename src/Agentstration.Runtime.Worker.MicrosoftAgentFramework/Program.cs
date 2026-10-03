using Agentstration.Runtime.Worker.MicrosoftAgentFramework;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
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
