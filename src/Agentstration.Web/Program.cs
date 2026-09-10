using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web;
using Agentstration.Web.Api;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Configuration;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Hosting;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var bootstrapOptions = new LocalBootstrapOptions();
var configuredAuthentication = builder.Configuration.GetSection("Agentstration:Authentication").Get<Agentstration.Web.Configuration.AuthenticationOptions>() ?? new();
if (string.Equals(configuredAuthentication.Mode, Agentstration.Web.Configuration.AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase))
{
    bootstrapOptions.ExternalIdentityIssuer = configuredAuthentication.DevelopmentIssuer;
    bootstrapOptions.ExternalIdentitySubject = configuredAuthentication.DevelopmentSubject;
    bootstrapOptions.PrincipalDisplayName = configuredAuthentication.DevelopmentDisplayName;
}
var genAiObservability = builder.Configuration.GetSection(GenAiObservabilityOptions.SectionName).Get<GenAiObservabilityOptions>() ?? new();
genAiObservability.Validate(builder.Environment.IsDevelopment());
var toolExecutionCapture = builder.Configuration.GetSection("Agentstration:ToolExecution").Get<ToolExecutionCaptureOptions>() ?? new();
toolExecutionCapture.Validate();
var isTesting = builder.Environment.IsEnvironment("Testing");
var hostedServicesEnabled = !isTesting
    || builder.Configuration.GetValue("Agentstration:Testing:HostedServicesEnabled", false);
var openTelemetryEnabled = !isTesting
    || builder.Configuration.GetValue("Agentstration:Testing:OpenTelemetryEnabled", false);
var configuredDataDirectory = builder.Configuration["Data:Directory"];
var ownsTestingDataDirectory = isTesting
    && (string.IsNullOrWhiteSpace(configuredDataDirectory)
        || string.Equals(configuredDataDirectory, ".agentstration", StringComparison.Ordinal));
var testingStorageDirectory = isTesting
    ? Path.Combine(Path.GetTempPath(), "agentstration-web-tests", Guid.NewGuid().ToString("N"))
    : null;
var dataDirectory = ownsTestingDataDirectory
    ? testingStorageDirectory!
    : configuredDataDirectory ?? Path.Combine(builder.Environment.ContentRootPath, ".agentstration");
if (ownsTestingDataDirectory) builder.Configuration["Data:Directory"] = dataDirectory;
Directory.CreateDirectory(dataDirectory);
var storageOptions = (builder.Configuration.GetSection(AgentstrationStorageOptions.SectionName).Get<AgentstrationStorageOptions>() ?? new()) with
{
    ConnectionString = builder.Configuration.GetConnectionString("Agentstration")
};
var storageProvider = storageOptions.GetProvider();
var identityConnectionString = storageProvider == AgentstrationStorageProvider.PostgreSql
    ? storageOptions.ConnectionString!
    : builder.Configuration.GetConnectionString("Identity")
        ?? $"Data Source={Path.Combine(testingStorageDirectory ?? dataDirectory, "identity.db")}";
var dataProtectionKeysPath = configuredAuthentication.DataProtectionKeysPath;
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionKeysPath = Path.Combine(testingStorageDirectory ?? dataDirectory, "data-protection-keys");
}
var aiProvider = builder.Configuration["AI:Provider"] ?? "Managed";
var useManagedProfileResolver = string.Equals(aiProvider, "Managed", StringComparison.OrdinalIgnoreCase);
const string defaultAiEndpoint = "http://localhost:11434/v1/";
var aiEndpoint = builder.Configuration["AI:Endpoint"] ?? defaultAiEndpoint;
if (!Uri.TryCreate(aiEndpoint.EndsWith('/') ? aiEndpoint : aiEndpoint + '/', UriKind.Absolute, out var parsedAiEndpoint)) throw new InvalidOperationException("AI:Endpoint must be an absolute URL.");
var aiOptions = new AiProviderOptions(aiProvider, parsedAiEndpoint, builder.Configuration["AI:Model"] ?? "phi4-mini", builder.Configuration["AI:ApiKey"]);
string? BuildSqliteConnectionString(string setting, string fileName)
{
    if (storageProvider == AgentstrationStorageProvider.PostgreSql) return null;
    var path = isTesting
        ? Path.Combine(testingStorageDirectory!, fileName)
        : builder.Configuration[setting] ?? Path.Combine(dataDirectory, fileName);
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    return $"Data Source={path}";
}
var controlPlaneConnectionString = BuildSqliteConnectionString("Data:ControlPlanePath", "control-plane.db");
var workPlaneConnectionString = BuildSqliteConnectionString("Data:WorkPlanePath", "work-plane.db");
var flowConnectionString = BuildSqliteConnectionString("Data:FlowPath", "flow-plane.db");
var runtimeConnectionString = BuildSqliteConnectionString("Data:RuntimePath", "runtime-plane.db");
var sourceVerificationIndexOptions = builder.Configuration
    .GetSection(Agentstration.Infrastructure.Sources.SourceVerificationIndexOptions.SectionName)
    .Get<Agentstration.Infrastructure.Sources.SourceVerificationIndexOptions>() ?? new();
var platformRegistrationOptions = new AgentstrationServiceRegistrationOptions
{
    DataDirectory = dataDirectory,
    AiOptions = aiOptions,
    ControlPlaneConnectionString = controlPlaneConnectionString,
    WorkPlaneConnectionString = workPlaneConnectionString,
    FlowConnectionString = flowConnectionString,
    RuntimeConnectionString = runtimeConnectionString,
    StorageOptions = storageOptions,
    EnableHostedServices = hostedServicesEnabled,
    SourceVerificationIndexOptions = sourceVerificationIndexOptions
};
var testingSqliteConnectionStrings = testingStorageDirectory is not null
    && storageProvider == AgentstrationStorageProvider.Sqlite
        ? new[]
        {
            identityConnectionString,
            controlPlaneConnectionString!,
            workPlaneConnectionString!,
            flowConnectionString!,
            runtimeConnectionString!
        }
        : [];
builder.Services.AddAgentstrationWebHost(
    builder.Configuration,
    builder.Environment,
    new WebHostServiceRegistrationOptions(
        bootstrapOptions,
        toolExecutionCapture,
        platformRegistrationOptions,
        storageProvider,
        identityConnectionString,
        dataProtectionKeysPath,
        testingStorageDirectory,
        testingSqliteConnectionStrings,
        useManagedProfileResolver));
builder.AddAgentstrationObservability(openTelemetryEnabled);

var app = builder.Build();
var testingDataDirectoryCleanup = testingStorageDirectory is not null
    ? app.Services.GetRequiredService<TestingDataDirectoryCleanup>()
    : null;
if (genAiObservability.HttpPayloadCapture.Enabled)
{
    app.Logger.LogWarning(
        "Advanced AI HTTP payload capture is enabled with a maximum body length of {MaximumBodyLength}. Request payloads may contain sensitive data",
        genAiObservability.HttpPayloadCapture.MaximumBodyLength);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRequestLocalization();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<PrincipalResolutionMiddleware>();
app.UseMiddleware<RequestContextMiddleware>();
app.UseMiddleware<StandardManagementDataMiddleware>();
app.UseAuthorization();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapAgentstrationOpenApi();
app.UseAntiforgery();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/health/ready", (IAgentstrationStorageInitializer storage) => storage.IsReady
    ? Results.Ok(new { status = "ready" })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
app.MapAgentstrationCultureEndpoint().AllowAnonymous();
app.MapAgentstrationAuthentication();
app.MapAgentstrationLocalAccountAdministration();
app.MapAgentstrationIdentityApi();
app.MapAgentstrationBootstrapProfiles();
app.MapAgentstrationManagementApi();
app.MapAgentstrationModelManagementApi();
app.MapAgentstrationAepEnrollment();
app.MapAgentstrationWorkApi();
app.MapAgentstrationWorkplaceApi();
app.MapAgentstrationWorkOperationsApi();
app.MapAgentstrationFlowApi();
app.MapAgentstrationRuntimeApi();
app.MapAgentstrationToolGovernanceAuditApi();
app.MapHub<FlowRunHub>("/hubs/flow-runs").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
app.MapHub<WorkplaceHub>("/hubs/workplace").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);
if (app.Environment.IsDevelopment()) app.MapOllamaDiagnostics();
app.MapMcp("/mcp").RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(MainLayout).Assembly).AddInteractiveServerRenderMode()
    .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
try
{
    RequestContext? bootstrapContext = null;
    var startupScopes = app.Services.GetRequiredService<IRequestContextScopeFactory>();
    using (startupScopes.PushSystem())
    {
        await app.Services.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<AgentManagementService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<LocalIdentityDatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
        if (string.Equals(configuredAuthentication.Mode, Agentstration.Web.Configuration.AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase))
            bootstrapContext = await app.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<WorkItemService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<WorkplaceService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<FlowService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<FlowRunService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<RuntimeRunService>().InitializeAsync(app.Lifetime.ApplicationStopping);
        if (builder.Configuration.GetValue("Agentstration:Extensions:DiscoverOnStartup", false))
            await app.Services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverForActiveWorkspacesAsync(app.Lifetime.ApplicationStopping);
        await app.Services.ApplyDeclarativeBootstrapAsync(app.Lifetime.ApplicationStopping);
        if (builder.Configuration.GetValue("Agentstration:Extensions:DiscoverOnStartup", false))
            await app.Services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverForActiveWorkspacesAsync(app.Lifetime.ApplicationStopping);
    }
    if (bootstrapContext is not null)
        await WorkspaceStartupData.InitializeAsync(
            app.Services,
            bootstrapContext,
            includeInteractiveDemo: !app.Environment.IsEnvironment("Testing"),
            app.Lifetime.ApplicationStopping);
}
catch
{
    testingDataDirectoryCleanup?.Dispose();
    throw;
}
await app.RunAsync();

public partial class Program;
