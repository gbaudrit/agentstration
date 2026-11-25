using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Coding.Contracts;
using Agentstration.Http.Diagnostics;
using Agentstration.Runtime.Worker.Coding;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

if (args.Length == 1 && args[0] == "--native-helper")
{
    try { Environment.ExitCode = await CodingNativeHelper.RunAsync(TimeProvider.System, CancellationToken.None); }
    catch (Exception exception) when (exception is IOException or JsonException or CodingProcessException or UnauthorizedAccessException)
    { Console.Error.WriteLine("coding_helper_input_invalid"); Environment.ExitCode = 78; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentstrationHttpDiagnostics();
builder.Services.AddOptions<RuntimeWorkerOptions>().Bind(builder.Configuration.GetSection(RuntimeWorkerOptions.SectionName))
    .Validate(value => value.Validate() && value.MaximumConcurrentAssignments == 1, "The single-assignment Coding Worker configuration is invalid.").ValidateOnStart();
builder.Services.AddOptions<CodingWorkerOptions>().Bind(builder.Configuration.GetSection(CodingWorkerOptions.SectionName))
    .Validate(value => value.Validate(), "The Coding CLI, cache or Linux isolation configuration is invalid.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("agentstration-awp"); builder.Services.AddHttpClient("agentstration-awp-enrollment");
builder.Services.AddSingleton<RuntimeWorkerReadiness>(); builder.Services.AddSingleton<AwpWorkerCredentialProvider>();
builder.Services.AddSingleton<CodingSandbox>(); builder.Services.AddSingleton<IAwpAssignmentExecutor, CodingAssignmentExecutor>();
builder.Services.AddSingleton<AwpAssignmentRunner>();
builder.Services.AddSingleton(new AwpWorkerRuntimeDescriptor(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    [new(CodingExecutionProtocol.RuntimeCapability, CodingExecutionProtocol.CapabilityVersion, [CodingExecutionProtocol.MaterialVersion], "codex-materializer/1.0.0")]));
builder.Services.AddHostedService<CodingPrerequisiteCheck>();
builder.Services.AddHostedService<AwpRuntimeWorkerService>();
builder.Services.AddHealthChecks().AddCheck<RuntimeWorkerLivenessCheck>("runtime-worker-live", tags: ["live"])
    .AddCheck<RuntimeWorkerReadinessCheck>("runtime-worker-ready", tags: ["ready"]);
var app = builder.Build(); app.UseAgentstrationHttpDiagnostics();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = value => value.Tags.Contains("live", StringComparer.Ordinal) });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = value => value.Tags.Contains("ready", StringComparer.Ordinal) });
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
