using Agentstration.Web.Components;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Components.State;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Web;
using Agentstration.Workplace.Web.Components;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var apiValue = builder.Configuration["Agentstration:ApiBaseUrl"] ?? throw new InvalidOperationException("Agentstration:ApiBaseUrl is required.");
if (!Uri.TryCreate(apiValue, UriKind.Absolute, out var apiUrl) || apiUrl.Scheme is not ("http" or "https")) throw new InvalidOperationException("Agentstration:ApiBaseUrl must be an absolute HTTP(S) URL.");
var hubValue = builder.Configuration["Agentstration:WorkplaceHubUrl"];
hubValue = string.IsNullOrWhiteSpace(hubValue) ? new Uri(apiUrl, "hubs/workplace").ToString() : hubValue;
if (!Uri.TryCreate(hubValue, UriKind.Absolute, out var hubUrl) || hubUrl.Scheme is not ("http" or "https")) throw new InvalidOperationException("Agentstration:WorkplaceHubUrl must be an absolute HTTP(S) URL.");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAgentstrationWebComponents();
builder.Services.AddAgentstrationLocalization(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient(provider => new WorkplaceApiSessionHandler(
    provider.GetRequiredService<IHttpContextAccessor>(),
    apiUrl,
    ".Agentstration.Identity.Application",
    "agentstration.workspace"));
builder.Services.AddScoped<IWorkplaceRealtimeConnectionOptionsConfigurator>(provider => new WorkplaceRealtimeSession(
    provider.GetRequiredService<IHttpContextAccessor>(),
    hubUrl,
    ".Agentstration.Identity.Application",
    "agentstration.workspace"));
builder.Services.AddAgentstrationWorkplaceClient(apiUrl, hubUrl)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<WorkplaceApiSessionHandler>();
builder.Services.AddHttpClient<IUserPreferencesClient, HttpUserPreferencesClient>(client => client.BaseAddress = apiUrl)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<WorkplaceApiSessionHandler>();
builder.Services.AddProblemDetails(); builder.Services.AddHealthChecks();
var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry().ConfigureResource(value => value.AddService("Agentstration.Workplace.Web")).WithTracing(value => { value.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(); if (otlp) value.AddOtlpExporter(); }).WithMetrics(value => { value.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(); if (otlp) value.AddOtlpExporter(); });
var app = builder.Build(); app.UseExceptionHandler(); app.UseStatusCodePages(); app.UseRequestLocalization(); app.UseAntiforgery(); app.MapHealthChecks("/health"); app.MapAgentstrationCultureEndpoint(); app.MapStaticAssets(); app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); await app.RunAsync();
public partial class Program;
