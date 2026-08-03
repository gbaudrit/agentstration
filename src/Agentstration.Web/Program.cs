using Agentstration.Application.Ingestion;
using Agentstration.Application.Missions;
using Agentstration.Application.Workflows;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.ModelProviders.Ollama;
using Agentstration.Runtime.Core;
using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Web;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Hosting;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["Data:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "data.json");
var aiProvider = builder.Configuration["AI:Provider"] ?? "Deterministic";
var defaultAiEndpoint = string.Equals(aiProvider, "Ollama", StringComparison.OrdinalIgnoreCase)
    ? "http://localhost:11434"
    : "http://localhost:11434/v1/";
var aiEndpoint = builder.Configuration["AI:Endpoint"] ?? defaultAiEndpoint;
if (!Uri.TryCreate(aiEndpoint.EndsWith('/') ? aiEndpoint : aiEndpoint + '/', UriKind.Absolute, out var parsedAiEndpoint)) throw new InvalidOperationException("AI:Endpoint must be an absolute URL.");
var aiOptions = new AiProviderOptions(aiProvider, parsedAiEndpoint, builder.Configuration["AI:Model"] ?? "phi4-mini", builder.Configuration["AI:ApiKey"]);
var controlPlanePath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"agentstration-tests-{Guid.NewGuid():N}.db")
    : builder.Configuration["Data:ControlPlanePath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "control-plane.db");
var controlPlaneDirectory = Path.GetDirectoryName(controlPlanePath);
if (!string.IsNullOrWhiteSpace(controlPlaneDirectory)) Directory.CreateDirectory(controlPlaneDirectory);
var workPlanePath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"agentstration-work-tests-{Guid.NewGuid():N}.db")
    : builder.Configuration["Data:WorkPlanePath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "work-plane.db");
var workPlaneDirectory = Path.GetDirectoryName(workPlanePath);
if (!string.IsNullOrWhiteSpace(workPlaneDirectory)) Directory.CreateDirectory(workPlaneDirectory);
var flowPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"agentstration-flow-tests-{Guid.NewGuid():N}.db")
    : builder.Configuration["Data:FlowPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "flow-plane.db");
var flowDirectory = Path.GetDirectoryName(flowPath);
if (!string.IsNullOrWhiteSpace(flowDirectory)) Directory.CreateDirectory(flowDirectory);
var runtimePath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"agentstration-runtime-tests-{Guid.NewGuid():N}.db")
    : builder.Configuration["Data:RuntimePath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "runtime-plane.db");
var runtimeDirectory = Path.GetDirectoryName(runtimePath);
if (!string.IsNullOrWhiteSpace(runtimeDirectory)) Directory.CreateDirectory(runtimeDirectory);
builder.Services.AddAgentstrationModelProviders(builder.Configuration);
builder.Services.AddAgentstration(dataPath, builder.Environment.IsEnvironment("Testing"), aiOptions, $"Data Source={controlPlanePath}", $"Data Source={workPlanePath}", $"Data Source={flowPath}", $"Data Source={runtimePath}");
builder.Services.AddAgentstrationModelManagement();
if (string.Equals(aiProvider, "Ollama", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("local-chat")))
    {
        builder.Configuration[$"{ModelProviderConfigurationSections.Root}:Providers:ollama-local:Endpoint"] = parsedAiEndpoint.AbsoluteUri;
    }
    builder.AddOllamaModelProvider("local-chat", parsedAiEndpoint, aiOptions.Model);
}
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAgentstrationWebConsole(builder.Configuration);
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
builder.Services.AddHostedService<ItemProcessingWorker>();
builder.Services.AddHostedService<MissionSchedulerWorker>();
builder.Services.AddHostedService<AgentDeploymentReconciliationWorker>();
builder.Services.AddHostedService<LocalWorkExecutionWorker>();
builder.Services.AddHostedService<RuntimeRunExecutionWorker>();

var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Agentstration.Web"))
    .WithTracing(tracing =>
    {
        tracing
        .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(IngestionService.ActivitySource.Name, ContentProcessingWorkflow.ActivitySource.Name, MissionService.ActivitySource.Name, WorkItemService.ActivitySource.Name);
        if (otlpEnabled) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter(WorkItemService.Meter.Name);
        if (otlpEnabled) metrics.AddOtlpExporter();
    });

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapAgentstrationApi();
app.MapAgentstrationManagementApi();
app.MapAgentstrationModelManagementApi();
app.MapAgentstrationWorkApi();
app.MapAgentstrationFlowApi();
app.MapAgentstrationRuntimeApi();
if (app.Environment.IsDevelopment()) app.MapOllamaDiagnostics();
app.MapMcp("/mcp");
app.MapStaticAssets();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(MainLayout).Assembly).AddInteractiveServerRenderMode();
await app.Services.GetRequiredService<AgentManagementService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<WorkItemService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<FlowService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<RuntimeRunService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await ManagementDemoData.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
await DemoData.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
await app.RunAsync();

public partial class Program;
