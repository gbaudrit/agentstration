using Agentstration.Application.Ingestion;
using Agentstration.Application.Missions;
using Agentstration.Application.Work;
using Agentstration.Application.Workflows;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var bootstrapOptions = builder.Configuration.GetSection("Agentstration:Bootstrap").Get<LocalBootstrapOptions>() ?? new LocalBootstrapOptions();
var configuredAuthentication = builder.Configuration.GetSection("Agentstration:Authentication").Get<Agentstration.Web.Configuration.AuthenticationOptions>() ?? new();
if (string.Equals(configuredAuthentication.Mode, Agentstration.Web.Configuration.AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase))
{
    bootstrapOptions.ExternalIdentityIssuer = configuredAuthentication.DevelopmentIssuer;
    bootstrapOptions.ExternalIdentitySubject = configuredAuthentication.DevelopmentSubject;
    bootstrapOptions.PrincipalDisplayName = configuredAuthentication.DevelopmentDisplayName;
}
builder.Services.AddSingleton(bootstrapOptions);
var genAiObservability = builder.Configuration.GetSection(GenAiObservabilityOptions.SectionName).Get<GenAiObservabilityOptions>() ?? new();
genAiObservability.Validate(builder.Environment.IsDevelopment());
var dataPath = builder.Configuration["Data:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration", "data.json");
var identityConnectionString = builder.Configuration.GetConnectionString("Identity")
    ?? (builder.Environment.IsEnvironment("Testing")
        ? $"Data Source={Path.Combine(Path.GetTempPath(), $"agentstration-identity-tests-{Guid.NewGuid():N}.db")}"
        : $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "identity.db")}");
var dataProtectionKeysPath = configuredAuthentication.DataProtectionKeysPath;
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionKeysPath = builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), $"agentstration-data-protection-tests-{Guid.NewGuid():N}")
        : Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "data-protection-keys");
}
var aiProvider = builder.Configuration["AI:Provider"] ?? "Managed";
var useManagedProfileResolver = string.Equals(aiProvider, "Managed", StringComparison.OrdinalIgnoreCase)
    || string.Equals(aiProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
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
builder.Services.AddAgentstration(dataPath, builder.Environment.IsEnvironment("Testing"), aiOptions, $"Data Source={controlPlanePath}", $"Data Source={workPlanePath}", $"Data Source={flowPath}", $"Data Source={runtimePath}");
builder.Services.AddAgentstrationModelProviders(
    builder.Configuration,
    useManagedProfileResolver);
builder.Services.AddAgentstrationModelManagement();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddAgentstrationLocalIdentity(identityConnectionString, dataProtectionKeysPath);
builder.Services.AddSingleton<IFlowRunEventSink, SignalRFlowRunEventSink>();
builder.Services.AddSingleton<IWorkplaceEventSink, SignalRWorkplaceEventSink>();
builder.Services.AddAgentstrationWebConsole(builder.Configuration, builder.Environment);
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
builder.Services.AddHostedService<ItemProcessingWorker>();
builder.Services.AddHostedService<MissionSchedulerWorker>();
builder.Services.AddHostedService<AgentDeploymentReconciliationWorker>();
builder.Services.AddHostedService<LocalWorkExecutionWorker>();
builder.Services.AddHostedService<RuntimeRunExecutionWorker>();
builder.Services.AddHostedService<FlowRunExecutionWorker>();

var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Agentstration.Web"));
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
    if (otlpEnabled) logging.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Agentstration.Web"))
    .WithTracing(tracing =>
    {
        tracing
        .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(
                IngestionService.ActivitySource.Name,
                ContentProcessingWorkflow.ActivitySource.Name,
                MissionService.ActivitySource.Name,
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
                AgentFrameworkRuntimeFactory.TelemetrySourceName,
                GenAiObservabilityOptions.ChatClientSourceName);
        if (otlpEnabled) metrics.AddOtlpExporter();
    });

var app = builder.Build();
if (genAiObservability.HttpPayloadCapture.Enabled)
{
    app.Logger.LogWarning(
        "Advanced AI HTTP payload capture is enabled with a maximum body length of {MaximumBodyLength}. Request payloads may contain sensitive data",
        genAiObservability.HttpPayloadCapture.MaximumBodyLength);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi();
app.UseAuthentication();
app.UseMiddleware<PrincipalResolutionMiddleware>();
app.UseMiddleware<RequestContextMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapAgentstrationAuthentication();
app.MapAgentstrationLocalAccountAdministration();
app.MapAgentstrationApi();
app.MapAgentstrationIdentityApi();
app.MapAgentstrationManagementApi();
app.MapAgentstrationModelManagementApi();
app.MapAgentstrationWorkApi();
app.MapAgentstrationWorkplaceApi();
app.MapAgentstrationWorkOperationsApi();
app.MapAgentstrationFlowApi();
app.MapAgentstrationRuntimeApi();
app.MapHub<FlowRunHub>("/hubs/flow-runs").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
app.MapHub<WorkplaceHub>("/hubs/workplace");
if (app.Environment.IsDevelopment()) app.MapOllamaDiagnostics();
app.MapMcp("/mcp");
app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(MainLayout).Assembly).AddInteractiveServerRenderMode()
    .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
await app.Services.GetRequiredService<AgentManagementService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<LocalIdentityDatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
if (string.Equals(configuredAuthentication.Mode, Agentstration.Web.Configuration.AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase)
    || string.Equals(configuredAuthentication.Mode, Agentstration.Web.Configuration.AuthenticationOptions.Oidc, StringComparison.OrdinalIgnoreCase))
    await app.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<WorkItemService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<WorkplaceService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<FlowService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<FlowRunService>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<RuntimeRunService>().InitializeAsync(app.Lifetime.ApplicationStopping);
if (app.Services.GetRequiredService<ICurrentRequestContext>().IsInitialized)
{
    await ManagementDemoData.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
    await WorkplaceDemoData.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
    await DemoData.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
}
await app.RunAsync();

public partial class Program;
